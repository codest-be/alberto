using Alberto.Benchmarks.Core;
using Xunit;

namespace Alberto.Tests.Benchmarks;

public class EventPlanTests
{
    [Fact]
    public void The_plan_contains_exactly_the_requested_number_of_events()
    {
        Assert.Equal(500, EventPlan.Build(count: 500, seed: 42).Count());
    }

    [Fact]
    public void The_same_seed_produces_an_identical_plan()
    {
        var first = EventPlan.Build(count: 1000, seed: 42);
        var second = EventPlan.Build(count: 1000, seed: 42);

        Assert.Equal(second.Select(e => e.EventType.Id), first.Select(e => e.EventType.Id));

        Assert.Equal(second.SelectMany(e => e.Tags).Select(t => t.Value), first.SelectMany(e => e.Tags).Select(t => t.Value));
    }

    [Fact]
    public void A_different_seed_produces_a_different_type_distribution()
    {
        var first = EventPlan.Build(count: 1000, seed: 42).Select(e => e.EventType.Id);
        var second = EventPlan.Build(count: 1000, seed: 43).Select(e => e.EventType.Id);

        Assert.NotEqual(second, first);
    }

    [Fact]
    public void Events_are_spread_across_the_declared_event_types()
    {
        var types = EventPlan.Build(count: 1000, seed: 42)
            .Select(e => e.EventType.Id)
            .Distinct()
            .ToList();

        Assert.Equivalent(EventPlan.TypeIds, types, strict: true);
    }

    [Fact]
    public void Tags_fan_out_across_the_declared_number_of_orders()
    {
        var tags = EventPlan.Build(count: 1000, seed: 42)
            .SelectMany(e => e.Tags)
            .Select(t => t.Value)
            .Distinct()
            .ToList();

        Assert.Equal(EventPlan.DistinctOrders, tags.Count());
    }

    [Fact]
    public void Every_event_carries_exactly_one_tag()
    {
        var items = EventPlan.Build(count: 200, seed: 42);
        Assert.NotEmpty(items);
        Assert.All(items, e => Assert.True(e.Tags.Count == 1));
    }

    [Fact]
    public void Every_event_carries_non_empty_json_data()
    {
        var items = EventPlan.Build(count: 200, seed: 42);
        Assert.NotEmpty(items);
        Assert.All(items, e => Assert.True(e.EventData.StartsWith("{") && e.EventData.EndsWith("}")));
    }

    [Fact]
    public void A_negative_count_is_rejected()
    {
        var act = () => EventPlan.Build(count: -1, seed: 42);

        Assert.ThrowsAny<ArgumentOutOfRangeException>(act);
    }
}
