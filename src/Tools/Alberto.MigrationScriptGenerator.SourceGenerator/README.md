# Alberto Migration Script Generator

**Automatic PostgreSQL migration generation at build time for Alberto EventStore and Projections**

## Overview

This source generator automatically detects your EventStore and Projection configurations, analyzes your projector
types, and generates PostgreSQL migration scripts during compilation. No more manual migration script management!

## Features

✅ **Automatic Schema Detection** - Discovers all schemas from your `PostgresEventStoreOptions` and
`PostgresProjectionOptions` configurations  
✅ **Projector Discovery** - Finds all types implementing `IProjector<TState>` and generates their projection tables  
✅ **EventStore Migrations** - Generates complete event store schema with all indexes and subscription tables  
✅ **Projection Migrations** - Creates projection tables with proper indexes for each discovered projector  
✅ **Multi-Schema Support** - Handles multiple schemas seamlessly  
✅ **Build-Time Generation** - No runtime overhead, migrations are ready before deployment

## Installation

### From NuGet (when published)

```bash
dotnet add package Alberto.MigrationScriptGenerator
```

### From Local Build

```bash
dotnet add reference /path/to/Alberto.MigrationScriptGenerator.SourceGenerator.csproj
```

## Usage

### 1. Reference the Package

Add the package reference to any project that uses Alberto EventStore or Projections:

```xml
<ItemGroup>
  <PackageReference Include="Alberto.MigrationScriptGenerator" Version="1.0.0" 
                    OutputItemType="Analyzer" 
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

### 2. Configure Your EventStore/Projections

The generator automatically detects schemas from your configuration:

```csharp
// In your Program.cs or Startup.cs
services.AddPostgresEventStore(options =>
{
    options.ConnectionString = connectionString;
    options.Schema = "tenant_a";  // ← Automatically detected!
});

services.AddPostgresProjections(options =>
{
    options.ConnectionString = connectionString;
    options.Schema = "tenant_a";  // ← Automatically detected!
});
```

### 3. Create Your Projectors

Define your projectors as usual:

```csharp
public class OrderProjector : IProjector<OrderState>
{
    public Task Project(OrderState state, IEventEnvelope @event)
    {
        // Your projection logic
    }
}

public class OrderState
{
    public string OrderId { get; set; }
    public decimal Total { get; set; }
}
```

### 4. Build Your Project

```bash
dotnet build
```

### 5. Find Generated Migrations

Migrations are automatically generated in `Migrations/Generated/`:

```
YourProject/
├── Migrations/
│   └── Generated/
│       ├── 001_eventstore_tenant_a.sql
│       ├── 001_projections_schema_tenant_a.sql
│       └── 002_projections_table_tenant_a_orderstate.sql
```

## Generated Files

### EventStore Migration (`001_eventstore_*.sql`)

- Creates schema
- Events table with all indexes
- Subscription checkpoints table
- Poison pills table

### Projection Schema Migration (`001_projections_schema_*.sql`)

- Creates schema (if not already created by EventStore)

### Projection Table Migrations (`002_projections_table_*.sql`)

- One per projector type per schema
- Includes primary key and indexes
- Optimized for multi-tenant queries

## Configuration Options

### Custom Output Path

Override the default output path in your `.csproj`:

```xml
<PropertyGroup>
  <AlbertoMigrationsOutputPath>$(MSBuildProjectDirectory)/Database/Migrations</AlbertoMigrationsOutputPath>
</PropertyGroup>
```

### Viewing Generated Source

Enable compiler-generated files output to see all generated code:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)/Generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```

## Multi-Schema Support

The generator automatically handles multiple schemas:

```csharp
// Tenant A
services.AddPostgresEventStore("tenant_a", options =>
{
    options.Schema = "tenant_a";
});

// Tenant B
services.AddPostgresEventStore("tenant_b", options =>
{
    options.Schema = "tenant_b";
});
```

This will generate migrations for both `tenant_a` and `tenant_b` schemas.

## Migration Manifest

A `MigrationManifest.g.cs` file is generated with metadata:

```csharp
namespace Alberto.Migrations;

public static class MigrationManifest
{
    public static readonly string[] Schemas = { "tenant_a", "tenant_b" };
    public static readonly string[] ProjectionTypes = { "OrderState", "CustomerState" };
}
```

Use this in your application to verify migrations or for diagnostics.

## Integration with Migration Tools

### DbUp

```csharp
var upgrader = DeployChanges.To
    .PostgresqlDatabase(connectionString)
    .WithScriptsFromFileSystem("Migrations/Generated")
    .LogToConsole()
    .Build();

var result = upgrader.PerformUpgrade();
```

### Fluent Migrator

```csharp
serviceProvider
    .GetRequiredService<IMigrationRunner>()
    .MigrateUp();
```

### EF Core Migrations

Copy the generated SQL files to your `Migrations` folder and run them manually or in a data migration.

## Troubleshooting

### No migrations generated?

1. Ensure you have EventStore or Projections configured
2. Check that schema values are string literals (not variables)
3. Build the project (not just restore)

### Wrong schemas detected?

The generator only detects schemas from compile-time constants. Ensure your schema configuration uses string literals:

```csharp
// ✅ Works
options.Schema = "tenant_a";

// ❌ Won't be detected
var schemaName = GetSchemaFromConfig();
options.Schema = schemaName;
```

## How It Works

1. **Build Time**: Source generator runs during compilation
2. **Syntax Analysis**: Scans for `AddPostgresEventStore` and `AddPostgresProjections` calls
3. **Schema Extraction**: Extracts schema names from configuration lambdas
4. **Type Discovery**: Finds all `IProjector<TState>` implementations
5. **SQL Generation**: Creates migration scripts for all schemas and projectors
6. **Output**: Writes SQL files to `Migrations/Generated/`

## License

Same as Alberto framework.

