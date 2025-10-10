using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Alberto.MigrationScriptGenerator.SourceGenerator;

[Generator]
public class MigrationScriptGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all IProjector<TState> implementations with [GenerateMigration] attribute
        var projectionTypesWithAttribute = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => s is ClassDeclarationSyntax,
                transform: static (ctx, _) => GetProjectorWithAttribute(ctx))
            .Where(static m => m != null);

        // Detect EventStore schemas from configuration
        var eventStoreSchemas = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsEventStoreConfiguration(s),
                transform: static (ctx, _) => ExtractEventStoreSchema(ctx))
            .Where(static s => s != null);

        // Collect all data
        var allData = projectionTypesWithAttribute.Collect()
            .Combine(eventStoreSchemas.Collect());

        // Generate migration scripts
        context.RegisterSourceOutput(allData, (spc, source) =>
        {
            var projectionTypes = source.Left.Where(p => p != null).Select(p => p!).ToList();
            var eventStoreSchemasList = source.Right.Where(s => s != null).Distinct().ToList();

            GenerateMigrations(spc, projectionTypes, eventStoreSchemasList);
        });
    }

    private static ProjectionTypeInfo? GetProjectorWithAttribute(GeneratorSyntaxContext context)
    {
        var classDecl = context.Node as ClassDeclarationSyntax;
        if (classDecl == null) return null;

        var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
        if (symbol == null) return null;

        // Check if class implements IProjector<TState>
        var projectorInterface = symbol.AllInterfaces
            .FirstOrDefault(i => i.Name == "IProjector" && i.TypeArguments.Length == 1);

        if (projectorInterface == null) return null;

        // Get TState type argument
        var stateType = projectorInterface.TypeArguments[0] as INamedTypeSymbol;
        if (stateType == null) return null;

        // Find the GenerateMigration attribute on the projector class
        var attribute = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "GenerateMigrationAttribute" ||
                                 a.AttributeClass?.Name == "GenerateMigration");

        if (attribute == null) return null;

        // Extract schema and table name from attribute
        string? schema = null;
        string? tableName = null;

        foreach (var namedArg in attribute.NamedArguments)
        {
            if (namedArg.Key == "Schema" && namedArg.Value.Value is string s)
                schema = s;
            else if (namedArg.Key == "TableName" && namedArg.Value.Value is string t)
                tableName = t;
        }

        if (string.IsNullOrEmpty(schema))
            return null;

        // Use state type name in lowercase if table name not specified
        tableName ??= stateType.Name.ToLowerInvariant();

        return new ProjectionTypeInfo(schema!, tableName, stateType.Name);
    }

    private static bool IsEventStoreConfiguration(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation) return false;
        var text = invocation.ToString();
        return text.Contains("AddPostgresEventStore") ||
               text.Contains("PostgresEventStoreOptions") ||
               text.Contains("AddEventStoreWithPostgres");
    }

    private static string? ExtractEventStoreSchema(GeneratorSyntaxContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Look for lambda configuration: options => { options.Schema = "xxx"; }
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is SimpleLambdaExpressionSyntax lambda)
            {
                if (lambda.Body is BlockSyntax block)
                {
                    foreach (var statement in block.Statements)
                    {
                        if (statement is ExpressionStatementSyntax exprStmt &&
                            exprStmt.Expression is AssignmentExpressionSyntax assignment)
                        {
                            var left = assignment.Left.ToString();
                            if (left.EndsWith(".Schema") || left == "Schema")
                            {
                                if (assignment.Right is LiteralExpressionSyntax literal)
                                {
                                    return literal.Token.ValueText;
                                }
                            }
                        }
                    }
                }
            }
        }

        return null;
    }

    private static void GenerateMigrations(
        SourceProductionContext context,
        List<ProjectionTypeInfo> projectionTypes,
        List<string?> eventStoreSchemas)
    {
        // Get all unique schemas from both event stores and projections
        var allSchemas = new HashSet<string>();
        allSchemas.UnionWith(eventStoreSchemas.Where(s => !string.IsNullOrEmpty(s))!);
        allSchemas.UnionWith(projectionTypes.Select(p => p.Schema));

        if (allSchemas.Count == 0)
            return; // Nothing to generate

        // Track migration numbers to maintain order
        var migrationTracker = new MigrationTracker();

        // 1. Generate EventStore migrations for each schema
        foreach (var schema in eventStoreSchemas.Where(s => !string.IsNullOrEmpty(s)))
        {
            var migrationKey = $"EventStore_{schema}";
            if (!migrationTracker.HasMigration(migrationKey))
            {
                var migrationNumber = migrationTracker.GetNextNumber();
                var sql = GenerateEventStoreMigration(schema!);
                var fileName = $"Migration_{migrationNumber:D3}_EventStore_{schema}.g.cs";
                var csharpWrapper = WrapSqlInCSharpClass(sql, migrationKey, migrationNumber);
                context.AddSource(fileName, SourceText.From(csharpWrapper, Encoding.UTF8));
                migrationTracker.AddMigration(migrationKey, migrationNumber);
            }
        }

        // 2. Generate Projection schema migrations (one per unique schema)
        var projectionSchemas = projectionTypes.Select(p => p.Schema).Distinct();
        foreach (var schema in projectionSchemas)
        {
            var migrationKey = $"ProjectionsSchema_{schema}";
            if (!migrationTracker.HasMigration(migrationKey))
            {
                var migrationNumber = migrationTracker.GetNextNumber();
                var sql = GenerateProjectionSchemaMigration(schema);
                var fileName = $"Migration_{migrationNumber:D3}_ProjectionsSchema_{schema}.g.cs";
                var csharpWrapper = WrapSqlInCSharpClass(sql, migrationKey, migrationNumber);
                context.AddSource(fileName, SourceText.From(csharpWrapper, Encoding.UTF8));
                migrationTracker.AddMigration(migrationKey, migrationNumber);
            }
        }

        // 3. Generate Projection table migrations (one per projection type)
        foreach (var projection in projectionTypes.OrderBy(p => p.Schema).ThenBy(p => p.TableName))
        {
            var migrationKey = $"ProjectionTable_{projection.Schema}_{projection.TableName}";
            if (!migrationTracker.HasMigration(migrationKey))
            {
                var migrationNumber = migrationTracker.GetNextNumber();
                var sql = GenerateProjectionTableMigration(projection.Schema, projection.TableName);
                var fileName =
                    $"Migration_{migrationNumber:D3}_ProjectionTable_{projection.Schema}_{projection.TableName}.g.cs";
                var csharpWrapper = WrapSqlInCSharpClass(sql, migrationKey, migrationNumber);
                context.AddSource(fileName, SourceText.From(csharpWrapper, Encoding.UTF8));
                migrationTracker.AddMigration(migrationKey, migrationNumber);
            }
        }

        // 4. Generate a manifest file listing all migrations
        GenerateMigrationManifest(context, migrationTracker, projectionTypes);
    }

    private static void GenerateMigrationManifest(
        SourceProductionContext context,
        MigrationTracker tracker,
        List<ProjectionTypeInfo> projectionTypes)
    {
        var manifestBuilder = new StringBuilder();
        manifestBuilder.AppendLine("// <auto-generated/>");
        manifestBuilder.AppendLine("// This file is auto-generated by Alberto.MigrationScriptGenerator");
        manifestBuilder.AppendLine();
        manifestBuilder.AppendLine("namespace Alberto.Migrations.Generated;");
        manifestBuilder.AppendLine();
        manifestBuilder.AppendLine("/// <summary>");
        manifestBuilder.AppendLine("/// Auto-generated migration manifest");
        manifestBuilder.AppendLine("/// </summary>");
        manifestBuilder.AppendLine("public static class MigrationManifest");
        manifestBuilder.AppendLine("{");
        manifestBuilder.AppendLine($"    public const int TotalMigrations = {tracker.GetTotalMigrations()};");
        manifestBuilder.AppendLine();
        manifestBuilder.AppendLine("    public static readonly string[] Migrations = {");

        foreach (var migration in tracker.GetAllMigrations())
        {
            manifestBuilder.AppendLine($"        \"{migration.Key}\",");
        }

        manifestBuilder.AppendLine("    };");
        manifestBuilder.AppendLine();
        manifestBuilder.AppendLine("    public static readonly string[] ProjectionTypes = {");

        foreach (var projection in projectionTypes)
        {
            manifestBuilder.AppendLine($"        \"{projection.TypeName}\",");
        }

        manifestBuilder.AppendLine("    };");
        manifestBuilder.AppendLine("}");

        context.AddSource("MigrationManifest.g.cs", SourceText.From(manifestBuilder.ToString(), Encoding.UTF8));
    }

    private static string WrapSqlInCSharpClass(string sql, string migrationKey, int migrationNumber)
    {
        var escapedSql = sql.Replace("\"", "\"\"");
        var sanitizedKey = migrationKey.Replace(".", "_").Replace("-", "_");

        return $@"// <auto-generated/>
#nullable enable

namespace Alberto.Migrations.Generated;

/// <summary>
/// Migration: {migrationKey}
/// Number: {migrationNumber}
/// </summary>
public sealed class Migration_{migrationNumber:D3}_{sanitizedKey} : Alberto.EventStore.Postgres.Migrations.IEventStoreMigration
{{
    public int Number => {migrationNumber};
    
    public string Key => ""{migrationKey}"";
    
    public string GetSql(string schema)
    {{
        // Replace placeholder with actual schema name
        return Sql.Replace(""{{schema}}"", schema);
    }}
    
    private const string Sql = @""{escapedSql}"";
}}
";
    }

    private static string GenerateEventStoreMigration(string schema)
    {
        return $@"-- =============================================================================
-- ALBERTO EVENT STORE SCHEMA FOR POSTGRESQL
-- =============================================================================
-- Schema: {{schema}}
-- =============================================================================

CREATE SCHEMA IF NOT EXISTS {{schema}};

-- Main events table with tenant support
CREATE TABLE IF NOT EXISTS {{schema}}.events
(
    position       BIGSERIAL PRIMARY KEY,
    id             UUID NOT NULL UNIQUE,
    tenant_id      VARCHAR(20) NOT NULL,
    event_type     TEXT NOT NULL,
    data           JSONB NOT NULL,
    tags           TEXT[] NOT NULL DEFAULT '{{}}',
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata       JSONB NOT NULL DEFAULT '{{}}'
);

-- Essential indexes
CREATE INDEX IF NOT EXISTS idx_{{schema}}_events_tenant_position ON {{schema}}.events (tenant_id, position DESC);
CREATE INDEX IF NOT EXISTS idx_{{schema}}_events_consistency ON {{schema}}.events (tenant_id, position) WHERE position > 0;
CREATE INDEX IF NOT EXISTS idx_{{schema}}_events_global_position ON {{schema}}.events (position) INCLUDE (tenant_id, event_type, tags, data, metadata, created_at);

-- Optimized tenant-first indexes
CREATE INDEX IF NOT EXISTS idx_{{schema}}_events_tenant_tags_gin ON {{schema}}.events (tenant_id, tags) WHERE array_length(tags, 1) > 0;
CREATE INDEX IF NOT EXISTS idx_{{schema}}_events_tenant_type_tags ON {{schema}}.events (tenant_id, event_type, tags) WHERE array_length(tags, 1) > 0;
CREATE INDEX IF NOT EXISTS idx_{{schema}}_events_tenant_tags_covering ON {{schema}}.events (tenant_id) INCLUDE (event_type, tags, data, metadata, created_at, position) WHERE array_length(tags, 1) > 0;

-- Subscription checkpoints
CREATE TABLE IF NOT EXISTS {{schema}}.subscription_checkpoints
(
    subscription_id VARCHAR PRIMARY KEY,
    position        BIGINT NULL,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_{{schema}}_subscription_checkpoints_updated ON {{schema}}.subscription_checkpoints (updated_at DESC);

-- Poison pills
CREATE TABLE IF NOT EXISTS {{schema}}.subscription_poison_pills
(
    id                  UUID PRIMARY KEY,
    subscription_id     VARCHAR NOT NULL,
    global_position     BIGINT NOT NULL,
    event_id            UUID NOT NULL,
    event_type          VARCHAR NOT NULL,
    event_data          JSONB NOT NULL,
    metadata            JSONB NOT NULL,
    error_message       TEXT NOT NULL,
    stack_trace         TEXT,
    retry_count         INT NOT NULL,
    first_failed_at     TIMESTAMPTZ NOT NULL,
    last_failed_at      TIMESTAMPTZ NOT NULL,
    resolved_at         TIMESTAMPTZ,
    resolved_by         VARCHAR,
    resolution_action   VARCHAR,
    resolution_notes    TEXT
);

CREATE INDEX IF NOT EXISTS idx_{{schema}}_poison_pills_subscription ON {{schema}}.subscription_poison_pills (subscription_id);
CREATE INDEX IF NOT EXISTS idx_{{schema}}_poison_pills_event ON {{schema}}.subscription_poison_pills (event_id);
CREATE INDEX IF NOT EXISTS idx_{{schema}}_poison_pills_unresolved ON {{schema}}.subscription_poison_pills (subscription_id, last_failed_at) WHERE resolved_at IS NULL;
";
    }

    private static string GenerateProjectionSchemaMigration(string schema)
    {
        return $@"-- =============================================================================
-- ALBERTO PROJECTIONS SCHEMA FOR POSTGRESQL
-- =============================================================================
-- Schema: {{schema}}
-- =============================================================================

CREATE SCHEMA IF NOT EXISTS {{schema}};
";
    }

    private static string GenerateProjectionTableMigration(string schema, string tableName)
    {
        return $@"-- =============================================================================
-- PROJECTION TABLE: {{schema}}.{tableName}
-- =============================================================================

CREATE TABLE IF NOT EXISTS {{schema}}.{tableName} (
    tenant_id TEXT NOT NULL,
    key TEXT NOT NULL,
    state JSONB NOT NULL,
    global_version BIGINT NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, key)
);

CREATE INDEX IF NOT EXISTS idx_{{schema}}_{tableName}_tenant_updated ON {{schema}}.{tableName}(tenant_id, updated_at);
CREATE INDEX IF NOT EXISTS idx_{{schema}}_{tableName}_global_version ON {{schema}}.{tableName}(tenant_id, key, global_version);

COMMENT ON TABLE {{schema}}.{tableName} IS 'Projection state for {tableName}';
";
    }

    private class ProjectionTypeInfo(string schema, string tableName, string typeName)
    {
        public string Schema { get; } = schema;
        public string TableName { get; } = tableName;
        public string TypeName { get; } = typeName;
    }

    private class MigrationTracker
    {
        private readonly Dictionary<string, int> _migrations = new();
        private int _currentNumber = 1;

        public bool HasMigration(string key) => _migrations.ContainsKey(key);

        public int GetNextNumber() => _currentNumber++;

        public void AddMigration(string key, int number)
        {
            _migrations[key] = number;
        }

        public int GetTotalMigrations() => _migrations.Count;

        public IEnumerable<KeyValuePair<string, int>> GetAllMigrations() =>
            _migrations.OrderBy(m => m.Value);
    }
}