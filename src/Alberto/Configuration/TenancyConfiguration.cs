using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;

namespace Alberto.Configuration;

/// <summary>
/// Overlays a module's <c>Tenancy</c> configuration section onto its code-declared
/// <see cref="TenancyDefinition"/>.
/// </summary>
/// <remarks>
/// A shard is declared in code and tuned from configuration: a shard named in both places starts
/// from the code declaration and takes configuration on top, the same precedence every other
/// Alberto option follows. A shard named <em>only</em> in configuration is recorded in
/// <see cref="TenancyDefinition.UndeclaredConfiguredShards"/> and reported as <c>ALB0015</c>, not
/// created. Services are registered while the service collection is still being built, which is
/// before any configuration is read, so a shard that first appears here would have no data source,
/// no migration and no control loops — a database that silently accepts nothing.
/// </remarks>
internal static class TenancyConfiguration
{
    internal const string SectionName = "Tenancy";
    internal const string ShardsSectionName = "Shards";
    internal const string CatalogSectionName = "Catalog";
    internal const string DefaultShardKey = "DefaultShard";
    internal const string RefreshIntervalKey = "CatalogRefreshInterval";

    internal static TenancyDefinition Apply(
        TenancyDefinition declared,
        IAlbertoBackendDescriptor? moduleBackend,
        IConfiguration moduleSection)
    {
        if (!declared.IsSharded)
            return declared;

        var tenancy = moduleSection.GetSection(SectionName);
        var shardsSection = tenancy.GetSection(ShardsSectionName);

        // Runs whether or not a Tenancy section exists: the module's own backend section is part
        // of every shard's layering, so a schema set once at module level reaches all of them.
        var result = declared with
        {
            Shards = OverlayShards(declared.Shards, moduleSection, shardsSection),
            UndeclaredConfiguredShards = shardsSection.GetChildren()
                .Select(s => s.Key)
                .Where(id => !declared.ShardIds.Contains(id, StringComparer.Ordinal))
                .ToImmutableArray(),
        };

        if (!tenancy.Exists())
            return result;

        var catalogSection = tenancy.GetSection(CatalogSectionName);
        if (catalogSection.Exists())
        {
            // The catalog is a database like any other, so it starts from whichever descriptor is
            // already there — an explicitly declared one, else the module's own backend, which at
            // least carries the right provider and pool defaults.
            var template = declared.Catalog ?? moduleBackend;
            if (template is not null)
                result = result with { Catalog = template.ApplyShardConfiguration(catalogSection) };
        }

        var defaultShard = tenancy[DefaultShardKey];
        if (!string.IsNullOrWhiteSpace(defaultShard))
            result = result with { DefaultShardId = defaultShard };

        var refresh = tenancy[RefreshIntervalKey];
        if (!string.IsNullOrWhiteSpace(refresh) && TimeSpan.TryParse(refresh, out var interval))
            result = result with { CatalogRefreshInterval = interval };

        return result;
    }

    /// <remarks>
    /// A shard's options are layered code template → shard code → module configuration → shard
    /// configuration. The module's own <c>Postgres</c> section reaches every shard, so settings
    /// that are genuinely module-wide — the schema, pool sizes — need writing once; the shard's
    /// section then carries what is actually per-database.
    /// </remarks>
    private static ImmutableArray<ShardDeclaration> OverlayShards(
        ImmutableArray<ShardDeclaration> declared,
        IConfiguration moduleSection,
        IConfiguration shardsSection)
    {
        var builder = ImmutableArray.CreateBuilder<ShardDeclaration>(declared.Length);

        foreach (var shard in declared)
        {
            var backend = shard.Backend.ApplyConfiguration(moduleSection);

            var section = shardsSection.GetSection(shard.ShardId);
            if (section.Exists())
                backend = backend.ApplyShardConfiguration(section);

            builder.Add(shard with { Backend = backend });
        }

        return builder.MoveToImmutable();
    }
}
