using Alberto.Benchmarks.Core;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Benchmarks;

public class EventPlanTests
{
    [Fact]
    public void The_plan_contains_exactly_the_requested_number_of_events()
    {
        EventPlan.Build(count: 500, seed: 42).Should().HaveCount(500);
    }

    [Fact]
    public void The_same_seed_produces_an_identical_plan()
    {
        var first = EventPlan.Build(count: 1000, seed: 42);
        var second = EventPlan.Build(count: 1000, seed: 42);

        first.Select(e => e.EventType.Id)
            .Should().Equal(second.Select(e => e.EventType.Id));

        first.SelectMany(e => e.Tags).Select(t => t.Value)
            .Should().Equal(second.SelectMany(e => e.Tags).Select(t => t.Value));
    }

    [Fact]
    public void A_different_seed_produces_a_different_type_distribution()
    {
        var first = EventPlan.Build(count: 1000, seed: 42).Select(e => e.EventType.Id);
        var second = EventPlan.Build(count: 1000, seed: 43).Select(e => e.EventType.Id);

        first.Should().NotEqual(second);
    }

    [Fact]
    public void Events_are_spread_across_the_declared_event_types()
    {
        var types = EventPlan.Build(count: 1000, seed: 42)
            .Select(e => e.EventType.Id)
            .Distinct()
            .ToList();

        types.Should().BeEquivalentTo(EventPlan.TypeIds);
    }

    [Fact]
    public void Tags_fan_out_across_the_declared_number_of_orders()
    {
        var tags = EventPlan.Build(count: 1000, seed: 42)
            .SelectMany(e => e.Tags)
            .Select(t => t.Value)
            .Distinct()
            .ToList();

        tags.Should().HaveCount(EventPlan.DistinctOrders);
    }

    [Fact]
    public void Every_event_carries_exactly_one_tag()
    {
        EventPlan.Build(count: 200, seed: 42).Should().OnlyContain(e => e.Tags.Count == 1);
    }

    [Fact]
    public void Every_event_carries_non_empty_json_data()
    {
        EventPlan.Build(count: 200, seed: 42)
            .Should().OnlyContain(e => e.EventData.StartsWith("{") && e.EventData.EndsWith("}"));
    }

    [Fact]
    public void A_negative_count_is_rejected()
    {
        var act = () => EventPlan.Build(count: -1, seed: 42);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
