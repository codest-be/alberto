namespace EventStore.Postgres;

public interface ISchemaContext
{
    string CurrentSchema { get; set; }
}