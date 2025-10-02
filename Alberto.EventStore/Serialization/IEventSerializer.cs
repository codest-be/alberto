namespace Alberto.EventStore.Serialization;

public interface IEventSerializer
{
    string Serialize<T>(T o);
}