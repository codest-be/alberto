using Alberto.Testing;
using Xunit;

namespace Alberto.Tests.Testing;

public class TestEventsTests
{
    [EventType("test-events-probe")]
    private record Probe(string Value) : IEvent;

    [Fact]
    public void NewEvent_ResolvesTheEventTypeIdFromTheAttribute()
    {
        var appended = TestEvents.NewEvent(new Probe("hello"));

        Assert.Equal("test-events-probe", appended.EventType.Id);
    }

    [Fact]
    public void NewEvent_SerializesThePayload()
    {
        var appended = TestEvents.NewEvent(new Probe("hello"));

        Assert.Contains("hello", appended.EventData);
    }

    [Fact]
    public void NewEvent_CarriesTags()
    {
        var appended = TestEvents.NewEvent(
            new Probe("hello"),
            tags: [new EventTag("order", "o-1")]);

        Assert.Contains(appended.Tags, t => t.Concept == "order" && t.Id == "o-1");
    }

    [Fact]
    public void NewEvent_GivesEachEventADistinctId()
    {
        Assert.NotEqual(
            TestEvents.NewEvent(new Probe("a")).Id,
            TestEvents.NewEvent(new Probe("b")).Id);
    }
}
