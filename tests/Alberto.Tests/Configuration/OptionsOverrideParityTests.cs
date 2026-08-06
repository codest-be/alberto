using System.Reflection;
using Alberto.Configuration;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Guards the hand-written nullable mirror records against drift. Adding a property to an
/// options record without adding it to the mirror silently makes that knob unconfigurable
/// from appsettings.json; this test turns that into a build failure instead.
/// </summary>
public class OptionsOverrideParityTests
{
    private static IReadOnlyList<(Type Overrides, Type Options)> DiscoverPairs()
    {
        var assemblies = new[]
        {
            typeof(ControlLoopOptions).Assembly,
            typeof(Alberto.Postgres.PostgresOptions).Assembly,
        };

        return assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAlbertoOverrides<>))
                .Select(i => (Overrides: t, Options: i.GetGenericArguments()[0])))
            .ToList();
    }

    private static PropertyInfo[] PublicProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0)
            .Where(p => p.Name != "EqualityContract")
            .ToArray();

    private static bool IsValidMirror(Type optionType, Type mirrorType)
    {
        if (Nullable.GetUnderlyingType(mirrorType) == optionType)
            return true;

        if (!optionType.IsValueType && mirrorType == optionType)
            return true;

        return mirrorType.GetInterfaces().Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IAlbertoOverrides<>)
            && i.GetGenericArguments()[0] == optionType);
    }

    [Fact]
    public void At_least_one_options_override_pair_is_discovered()
    {
        DiscoverPairs().Should().NotBeEmpty(
            "the parity test is worthless if it silently discovers nothing");
    }

    [Fact]
    public void Every_options_property_has_a_settable_nullable_mirror()
    {
        var problems = new List<string>();

        foreach (var (overridesType, optionsType) in DiscoverPairs())
        {
            // Computed properties (no SetMethod) are not configuration knobs and have no mirror.
            foreach (var prop in PublicProperties(optionsType).Where(p => p.SetMethod != null))
            {
                var mirror = overridesType.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance);

                if (mirror is null)
                {
                    problems.Add($"{overridesType.Name} is missing '{prop.Name}'.");
                    continue;
                }

                if (mirror.SetMethod?.IsPublic != true)
                {
                    problems.Add($"{overridesType.Name}.{prop.Name} needs a public setter so the configuration binder can write it.");
                    continue;
                }

                if (!IsValidMirror(prop.PropertyType, mirror.PropertyType))
                {
                    problems.Add(
                        $"{overridesType.Name}.{prop.Name} is '{mirror.PropertyType.Name}' but should be a nullable " +
                        $"'{prop.PropertyType.Name}' or an IAlbertoOverrides<{prop.PropertyType.Name}>.");
                }
            }
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Override_mirrors_have_no_properties_the_options_record_lacks()
    {
        var problems = new List<string>();

        foreach (var (overridesType, optionsType) in DiscoverPairs())
        {
            var known = PublicProperties(optionsType).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var mirror in PublicProperties(overridesType))
            {
                if (!known.Contains(mirror.Name))
                    problems.Add($"{overridesType.Name}.{mirror.Name} does not exist on {optionsType.Name}.");
            }
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }
}
