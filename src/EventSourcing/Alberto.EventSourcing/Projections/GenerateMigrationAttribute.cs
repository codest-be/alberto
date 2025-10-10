namespace Alberto.EventSourcing.Projections;

/// <summary>
/// Marks a projection state type to generate PostgreSQL migration scripts.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class GenerateMigrationAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the PostgreSQL schema name for this projection.
    /// </summary>
    public required string Schema { get; set; }

    /// <summary>
    /// Gets or sets the table name for this projection.
    /// If not specified, defaults to the type name in lowercase.
    /// </summary>
    public string? TableName { get; set; }
}