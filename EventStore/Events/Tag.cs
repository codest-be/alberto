namespace EventStore.Events;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public class Tag(string name) : Attribute
{
    public string Name { get; } = name;
}