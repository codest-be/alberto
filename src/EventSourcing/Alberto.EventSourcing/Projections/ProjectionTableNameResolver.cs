using System.Reflection;
using Alberto.EventSourcing.Projectors;

namespace Alberto.EventSourcing.Projections;

/// <summary>
/// Resolves projection table names consistently across migration generation and runtime.
/// Ensures that the table name used in migrations matches what the repository expects.
/// </summary>
public static class ProjectionTableNameResolver
{
    /// <summary>
    /// Resolves the table name for a projection state type by looking up its associated projector.
    /// </summary>
    /// <typeparam name="TState">The projection state type</typeparam>
    /// <param name="projectorType">The projector type that implements IProjector&lt;TState&gt;</param>
    /// <returns>The table name (without schema), including the "_projections" suffix</returns>
    public static string ResolveTableName<TState>(Type projectorType)
    {
        if (projectorType == null)
            throw new ArgumentNullException(nameof(projectorType));

        // Verify the projector implements IProjector<TState>
        var projectorInterface = projectorType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType &&
                                 i.GetGenericTypeDefinition() == typeof(IProjector<>) &&
                                 i.GetGenericArguments()[0] == typeof(TState));

        if (projectorInterface == null)
            throw new ArgumentException(
                $"Type {projectorType.Name} does not implement IProjector<{typeof(TState).Name}>",
                nameof(projectorType));

        return ResolveTableName(projectorType, typeof(TState));
    }

    /// <summary>
    /// Resolves the table name for a projection by examining the projector type.
    /// </summary>
    /// <param name="projectorType">The projector type</param>
    /// <param name="stateType">The state type</param>
    /// <returns>The table name (without schema), including the "_projections" suffix</returns>
    public static string ResolveTableName(Type projectorType, Type stateType)
    {
        // Check for GenerateMigration attribute on the projector
        var attr = projectorType.GetCustomAttribute<GenerateMigrationAttribute>();

        string tableName;
        if (attr != null && !string.IsNullOrEmpty(attr.TableName))
        {
            // Use explicitly specified table name
            tableName = attr.TableName;
        }
        else
        {
            // Fall back to state type name in lowercase
            tableName = stateType.Name.ToLowerInvariant();
        }

        // Ensure _projections suffix
        if (!tableName.EndsWith("_projections"))
            tableName += "_projections";

        return tableName;
    }

    /// <summary>
    /// Resolves the schema name for a projection by examining the projector type.
    /// </summary>
    /// <param name="projectorType">The projector type</param>
    /// <returns>The schema name, or null if not specified</returns>
    public static string? ResolveSchema(Type projectorType)
    {
        var attr = projectorType.GetCustomAttribute<GenerateMigrationAttribute>();
        return attr?.Schema;
    }
}