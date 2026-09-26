using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;

namespace Alberto.Configuration;

/// <summary>
/// A mutable, all-nullable mirror of an immutable options record. The configuration binder
/// writes into the mirror; <see cref="ApplyTo"/> folds the values that were actually present
/// onto the code-configured defaults.
/// </summary>
/// <typeparam name="TOptions">The immutable options record this type mirrors.</typeparam>
/// <remarks>
/// Every implementing type keeps its public properties and its interface list under trimming.
/// The startup configuration scan (<c>ALB0008</c>) reads both by reflection to tell a legal key
/// from a typo; were either trimmed away, every configured key would be reported as unknown.
/// </remarks>
[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces)]
public interface IAlbertoOverrides<TOptions>
    where TOptions : class
{
    /// <summary>
    /// Returns <paramref name="options"/> with every non-null override applied.
    /// Null properties leave the corresponding option untouched.
    /// </summary>
    TOptions ApplyTo(TOptions options);
}

/// <summary>
/// Binds an <see cref="IAlbertoOverrides{TOptions}"/> mirror from a configuration section and
/// applies it. Backend packages use this to overlay their own options records.
/// </summary>
public static class AlbertoOptionsOverlay
{
    /// <summary>
    /// Reads <paramref name="key"/> from <paramref name="parent"/> and applies it to
    /// <paramref name="current"/>. Returns <paramref name="current"/> unchanged when the
    /// section is absent.
    /// </summary>
    /// <remarks>
    /// Binds <typeparamref name="TOverrides"/> through the reflection-based configuration binder,
    /// because the configuration binding source generator cannot see through a generic type
    /// parameter. Under trimming or Native AOT use the overload that takes a <c>bind</c> delegate
    /// and call <c>section.Get&lt;YourOverrides&gt;()</c> on the concrete type inside it, which
    /// the generator does intercept.
    /// </remarks>
    [RequiresUnreferencedCode(ReflectionBindingMessage)]
    [RequiresDynamicCode(ReflectionBindingMessage)]
    public static TOptions Overlay<TOptions, TOverrides>(
        IConfiguration parent,
        string key,
        TOptions current)
        where TOptions : class
        where TOverrides : class, IAlbertoOverrides<TOptions>
        // The binder generator cannot bind a generic TOverrides, and says so (SYSLIB1104) for any
        // call it sees. Passing the reflection binder as a method group is deliberate: this is the
        // reflection path, annotated as such, and the trim-safe overload below is the other one.
        => Overlay(parent, key, current, ReflectionBinder<TOverrides>.Get);

    [RequiresUnreferencedCode(ReflectionBindingMessage)]
    [RequiresDynamicCode(ReflectionBindingMessage)]
    private static class ReflectionBinder<T>
    {
        public static readonly Func<IConfiguration, T?> Get = ConfigurationBinder.Get<T>;
    }

    /// <summary>
    /// Reads <paramref name="key"/> from <paramref name="parent"/>, binds it with
    /// <paramref name="bind"/> and applies the result to <paramref name="current"/>. Returns
    /// <paramref name="current"/> unchanged when the section is absent or binds to
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The trim- and AOT-safe form of <see cref="Overlay{TOptions, TOverrides}(IConfiguration, string, TOptions)"/>.
    /// Pass <c>static section =&gt; section.Get&lt;YourOverrides&gt;()</c> with the concrete type
    /// spelled out, and set <c>EnableConfigurationBindingGenerator</c> so that call is bound by
    /// generated code rather than by reflection.
    /// </remarks>
    public static TOptions Overlay<TOptions, TOverrides>(
        IConfiguration parent,
        string key,
        TOptions current,
        Func<IConfigurationSection, TOverrides?> bind)
        where TOptions : class
        where TOverrides : class, IAlbertoOverrides<TOptions>
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(bind);

        var section = parent.GetSection(key);
        if (!section.Exists())
            return current;

        var overrides = bind(section);
        return overrides is null ? current : overrides.ApplyTo(current);
    }

    private const string ReflectionBindingMessage =
        "Binds a generic TOverrides through the reflection-based configuration binder. Use the " +
        "Overlay overload that takes a bind delegate, calling section.Get<T>() on the concrete type.";
}
