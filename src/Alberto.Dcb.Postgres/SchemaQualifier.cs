using System.Text.RegularExpressions;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Helper for qualifying SQL identifiers with schema prefix.
/// </summary>
internal sealed class SchemaQualifier
{
    // Allowlist: lowercase letter start, lowercase alphanumeric + underscore, max 63 chars (PostgreSQL identifier limit).
    // Mirrors the tenant-id allowlist in TenantContext so validation is consistent.
    private static readonly Regex SchemaNamePattern =
        new(@"^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled, matchTimeout: TimeSpan.FromMilliseconds(100));

    private readonly string _prefix;

    /// <summary>
    /// Creates a new schema qualifier.
    /// </summary>
    /// <param name="schema">The schema name, or null/empty for default (public) schema.</param>
    public SchemaQualifier(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            Name = "public";
            _prefix = "";
            return;
        }

        ValidateName(schema);
        Name = schema;
        _prefix = $"{Quote(schema)}.";
    }

    /// <summary>
    /// Gets the bare schema name for catalog queries.
    /// The default, unqualified schema is represented as <c>public</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the validated and quoted schema prefix (e.g., <c>"orders".</c> or an empty string).
    /// </summary>
    public string Prefix => _prefix;

    /// <summary>
    /// Qualifies a table name with the schema prefix.
    /// </summary>
    public string Table(string tableName) => $"{_prefix}{tableName}";

    /// <summary>
    /// Qualifies a function name with the schema prefix.
    /// </summary>
    public string Function(string functionName) => $"{_prefix}{functionName}";

    /// <summary>
    /// Whether a custom schema is configured.
    /// </summary>
    public bool HasSchema => !string.IsNullOrEmpty(_prefix);

    /// <summary>
    /// Validates that <paramref name="schema"/> matches the allowlist pattern
    /// (<c>^[a-z][a-z0-9_]{0,62}$</c>) and returns a double-quoted PostgreSQL identifier
    /// safe for use in DDL statements.
    /// </summary>
    /// <param name="schema">The schema name to validate.</param>
    /// <returns>The double-quoted identifier, e.g. <c>"orders"</c>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="schema"/> does not match the allowlist.
    /// </exception>
    public static string ValidateAndQuote(string schema)
    {
        ValidateName(schema);
        return Quote(schema);
    }

    /// <summary>
    /// Validates that <paramref name="schema"/> matches the allowlist pattern without quoting.
    /// Use this before passing a schema name to DbUp's <c>WithVariable</c>, where the
    /// migration scripts already place the value in the correct SQL context and quoting
    /// would corrupt the substitution.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="schema"/> does not match the allowlist.
    /// </exception>
    public static void ValidateName(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (!IsValidName(schema))
            throw new ArgumentException(
                $"Schema name '{schema}' is invalid. " +
                "Schema names must start with a lowercase letter and contain only lowercase letters, " +
                "digits, and underscores, with a maximum length of 63 characters.",
                nameof(schema));
    }

    /// <summary>
    /// Returns whether <paramref name="schema"/> matches the PostgreSQL schema allowlist.
    /// </summary>
    internal static bool IsValidName(string schema) => SchemaNamePattern.IsMatch(schema);

    private static string Quote(string schema) => $"\"{schema}\"";
}
