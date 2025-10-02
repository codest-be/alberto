namespace Alberto.EventStore.Postgres;

public class PostgresEventStoreOptions
{
    public string ConnectionString { get; set; } = null!;
    public string Schema { get; set; } = "default";
    public int BulkInsertThreshold { get; set; } = 5;
}