using Microsoft.Extensions.Configuration;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// A mutable, all-nullable mirror of an immutable options record. The configuration binder
/// writes into the mirror; <see cref="ApplyTo"/> folds the values that were actually present
/// onto the code-configured defaults.
/// </summary>
/// <typeparam name="TOptions">The immutable options record this type mirrors.</typeparam>
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
    public static TOptions Overlay<TOptions, TOverrides>(
        IConfiguration parent,
        string key,
        TOptions current)
        where TOptions : class
        where TOverrides : class, IAlbertoOverrides<TOptions>
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(current);

        var section = parent.GetSection(key);
        if (!section.Exists())
            return current;

        var overrides = section.Get<TOverrides>();
        return overrides is null ? current : overrides.ApplyTo(current);
    }
}
