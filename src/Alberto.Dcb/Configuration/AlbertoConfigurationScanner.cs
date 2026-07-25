using System.Collections.Immutable;
using System.Reflection;
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.Configuration;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// Scans a module's configuration section for keys that do not correspond to any known
/// <see cref="IAlbertoOverrides{TOptions}"/> property. Called from
/// <see cref="AlbertoModuleDefinition.ApplyConfiguration"/> to populate
/// <see cref="AlbertoModuleDefinition.UnknownConfigurationKeys"/>.
/// </summary>
internal static class AlbertoConfigurationScanner
{
    private static readonly Type OverridesOpenType = typeof(IAlbertoOverrides<>);

    // Top-level sections that are always valid and whose Overrides types are known statically.
    // "Processors" is omitted here because it is handled specially (dynamic child keys).
    private static readonly IReadOnlyDictionary<string, Type> StaticTopLevel =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["ControlLoop"] = typeof(ControlLoopOverrides),
            ["Telemetry"]   = typeof(TelemetryOverrides),
            ["Checkpoints"] = typeof(CheckpointOverrides),
        };

    internal static ImmutableArray<UnknownConfigurationKey> Scan(
        IConfigurationSection moduleSection,
        IAlbertoBackendDescriptor? backend)
    {
        var findings = new List<UnknownConfigurationKey>();

        // Build the set of legal top-level section names → their Overrides type (null = special).
        var legalTopLevel = new Dictionary<string, Type?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in StaticTopLevel)
            legalTopLevel[k] = v;

        // "Processors" children are processor IDs (dynamic, never flagged); only their
        // leaf properties are validated against ProcessorExecutionOverrides.
        legalTopLevel["Processors"] = null;

        // "Tenancy" is scanned separately: its shard ids are dynamic, and its leaves are the
        // backend's own properties rather than a fixed overrides type.
        legalTopLevel[TenancyConfiguration.SectionName] = null;

        // Backend section (e.g. "Postgres" → PostgresOverrides).
        Type? backendOverrides = null;
        if (backend != null)
        {
            var (sectionName, overridesType) = backend.GetConfigurationSection();
            if (sectionName is not null)
            {
                legalTopLevel[sectionName] = overridesType;
                backendOverrides = overridesType;
            }
        }

        foreach (var child in moduleSection.GetChildren())
        {
            if (string.Equals(child.Key, TenancyConfiguration.SectionName, StringComparison.OrdinalIgnoreCase))
            {
                ScanTenancy(child, backendOverrides, findings);
                continue;
            }

            if (!legalTopLevel.TryGetValue(child.Key, out var overridesType))
            {
                // Unknown top-level section.
                findings.Add(new UnknownConfigurationKey(
                    child.Path,
                    FindBestMatch(child.Key, legalTopLevel.Keys)));
                continue;
            }

            if (overridesType is not null)
            {
                // Recurse into sections with a statically-known Overrides type.
                ScanSection(child, overridesType, findings);
            }
            else
            {
                // Processors: iterate child sections (processor IDs — dynamic, don't flag)
                // and validate each processor ID section's leaf keys.
                foreach (var processorSection in child.GetChildren())
                    ScanSection(processorSection, typeof(ProcessorExecutionOverrides), findings);
            }
        }

        return findings.ToImmutableArray();
    }

    /// <summary>
    /// Validates the <c>Tenancy</c> section. Shard ids under <c>Shards</c> are dynamic and are
    /// never flagged here — a shard named in configuration but not in code is a real problem, but
    /// it is <c>ALB0015</c>'s to report, with a message that can actually explain it.
    /// </summary>
    private static void ScanTenancy(
        IConfigurationSection tenancy,
        Type? backendOverridesType,
        List<UnknownConfigurationKey> findings)
    {
        var legal = new[]
        {
            TenancyConfiguration.ShardsSectionName,
            TenancyConfiguration.CatalogSectionName,
            TenancyConfiguration.DefaultShardKey,
            TenancyConfiguration.RefreshIntervalKey,
        };

        foreach (var child in tenancy.GetChildren())
        {
            if (!legal.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new UnknownConfigurationKey(child.Path, FindBestMatch(child.Key, legal)));
                continue;
            }

            if (backendOverridesType is null)
                continue;

            // A shard section and the catalog section both hold the backend's own properties.
            if (string.Equals(child.Key, TenancyConfiguration.ShardsSectionName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var shard in child.GetChildren())
                    ScanSection(shard, backendOverridesType, findings);
            }
            else if (string.Equals(child.Key, TenancyConfiguration.CatalogSectionName, StringComparison.OrdinalIgnoreCase))
            {
                ScanSection(child, backendOverridesType, findings);
            }
        }
    }

    // Checks every child key of <paramref name="section"/> against the properties of
    // <paramref name="overridesType"/>. When a child key is a property whose type is itself a
    // nullable IAlbertoOverrides<T>, recurses into that sub-section.
    private static void ScanSection(
        IConfigurationSection section,
        Type overridesType,
        List<UnknownConfigurationKey> findings)
    {
        // Build a case-insensitive map of legal property names from the Overrides type.
        var props = overridesType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var child in section.GetChildren())
        {
            if (!props.TryGetValue(child.Key, out var prop))
            {
                // Unknown key — compute a did-you-mean suggestion.
                findings.Add(new UnknownConfigurationKey(
                    child.Path,
                    FindBestMatch(child.Key, props.Keys)));
                continue;
            }

            // If the property type is a nullable IAlbertoOverrides<T>, recurse.
            var propType  = prop.PropertyType;
            var underlying = Nullable.GetUnderlyingType(propType) ?? propType;
            if (IsOverridesType(underlying) && child.GetChildren().Any())
                ScanSection(child, underlying, findings);
        }
    }

    private static bool IsOverridesType(Type type) =>
        type.GetInterfaces()
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == OverridesOpenType);

    /// <summary>
    /// Returns the item in <paramref name="candidates"/> whose Levenshtein distance from
    /// <paramref name="key"/> is closest and within <c>Math.Max(2, candidate.Length / 3)</c>.
    /// Returns <c>null</c> when no candidate meets the threshold.
    /// </summary>
    private static string? FindBestMatch(string key, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var distance = LevenshteinDistance(key, candidate);
            var threshold = Math.Max(2, candidate.Length / 3);
            if (distance <= threshold && distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private static int LevenshteinDistance(string s, string t)
    {
        var sLen = s.Length;
        var tLen = t.Length;

        if (sLen == 0) return tLen;
        if (tLen == 0) return sLen;

        // Use two rows to reduce memory allocation.
        var previous = new int[tLen + 1];
        var current  = new int[tLen + 1];

        for (var j = 0; j <= tLen; j++)
            previous[j] = j;

        for (var i = 1; i <= sLen; i++)
        {
            current[0] = i;
            for (var j = 1; j <= tLen; j++)
            {
                var cost = string.Compare(s, i - 1, t, j - 1, 1,
                    StringComparison.OrdinalIgnoreCase) == 0 ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[tLen];
    }
}
