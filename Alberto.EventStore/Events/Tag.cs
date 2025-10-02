namespace Alberto.EventStore.Events;

[AttributeUsage(AttributeTargets.Property)]
public class Tag(string name) : Attribute
{
    public string Name { get; } = name;
}