namespace EventStore.Postgres;

public class SchemaContext : ISchemaContext
{
    public string CurrentSchema { get; set; } = "default";
}