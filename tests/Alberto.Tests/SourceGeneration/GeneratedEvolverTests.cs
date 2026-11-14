using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Alberto.Tests.SampleEvents;
using Alberto.Upcasting;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests;

/// <summary>
/// The dispatcher's two ways in: the table the source generator writes for a partial evolver,
/// and the reflection path everything else takes. <see cref="SampleEvolver"/> is partial and
/// compiled with the generator; the evolvers declared here are not.
/// </summary>
public sealed class GeneratedEvolverTests
{
    private static readonly PlainNote Note = new(Guid.NewGuid(), "hello");
    private static readonly VersionedOrder Order = new(Guid.NewGuid(), 12.5m, "BE");

    [Fact]
    public void A_partial_evolver_carries_a_generated_table_for_itself()
    {
        IGeneratedEvolver<SampleState> generated = new SampleEvolver();

        generated.GeneratedFor.Should().Be<SampleEvolver>();
        generated.EvolveTable().Select(h => h.EventType).Should().Equal(typeof(PlainNote), typeof(VersionedOrder));
    }

    [Fact]
    public void The_generated_table_folds_what_reflection_folds()
    {
        IEvent[] history = [Note, Order, Note, Order with { Country = "NL" }];

        var generated = new SampleEvolver();
        var reflected = new ReflectedTwin();

        generated.HandledEventTypes.Should().BeEquivalentTo(reflected.HandledEventTypes);
        history.Aggregate(new SampleState(), generated.Evolve)
            .Should().Be(history.Aggregate(new SampleState(), reflected.Evolve))
            .And.Be(new SampleState { Notes = 2, Total = 25m, LastCountry = "NL" });
    }

    [Fact]
    public void The_dispatcher_takes_the_table_over_the_interfaces()
    {
        var evolver = new TableDisagrees();

        evolver.HandledEventTypes.Should().Equal("e2e-plain-note");
        evolver.Evolve(new SampleState(), Note).Notes.Should().Be(100, "the table's handler ran, not Apply");
        evolver.Evolve(new SampleState(), Order).Should().Be(new SampleState(), "the table does not list it");
    }

    [Fact]
    public void A_subclass_of_a_generated_evolver_falls_back_to_reflection_and_keeps_its_own_handlers()
    {
        var evolver = new DerivedFromGenerated();

        evolver.HandledEventTypes.Should().BeEquivalentTo("e2e-plain-note", "e2e-versioned-order", "generated-evolver-extra");
        evolver.Evolve(new SampleState(), new ExtraNote("x")).LastCountry.Should().Be("extra");
        evolver.Evolve(new SampleState(), Order).Total.Should().Be(12.5m);
    }

    // ---- upcasters reading the old shape with their own JsonTypeInfo ---------------------------

    // The event's own options are case-insensitive PascalCase, so a snake_case payload only reads
    // if the step's JsonTypeInfo, and not the event's, is used.
    private static readonly JsonTypeInfo<VersionedOrderV1> SnakeCaseV1 = (JsonTypeInfo<VersionedOrderV1>)new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    }.GetTypeInfo(typeof(VersionedOrderV1));

    public static TheoryData<string> Overloads => ["final", "intermediate"];

    [Theory]
    [MemberData(nameof(Overloads))]
    public void An_upcaster_step_reads_the_old_payload_with_the_JsonTypeInfo_it_was_given(string overload)
    {
        var builder = DeclareUpcaster.For<VersionedOrder>("e2e-versioned-order");
        builder = overload == "final"
            ? builder.From<VersionedOrderV1>(1, SnakeCaseV1, v1 => new VersionedOrder(v1.OrderId, v1.Amount, "BE"))
            : builder.From<VersionedOrderV1, VersionedOrder>(1, SnakeCaseV1, v1 => new VersionedOrder(v1.OrderId, v1.Amount, "BE"));

        var serializer = EventSerializer.FromRegistry(SampleEventsRegistry.Registry)
            .WithUpcasters(UpcasterRegistry.Create().Add(builder.Build()).Build());

        var orderId = Guid.NewGuid();
        var upcast = serializer.Deserialize(new Envelope(
            "e2e-versioned-order", 1, $$"""{"order_id":"{{orderId:D}}","amount":7.5}"""));

        upcast.Should().Be(new VersionedOrder(orderId, 7.5m, "BE"));
    }

    [Fact]
    public void An_upcaster_step_refuses_a_null_JsonTypeInfo()
    {
        var builder = DeclareUpcaster.For<VersionedOrder>("e2e-versioned-order");

        builder.Invoking(b => b.From<VersionedOrderV1>(1, null!, v1 => Order))
            .Should().Throw<ArgumentNullException>().WithParameterName("oldShape");
        builder.Invoking(b => b.From<VersionedOrderV1, VersionedOrder>(1, null!, v1 => Order))
            .Should().Throw<ArgumentNullException>().WithParameterName("oldShape");
    }

    private sealed record Envelope(string Slug, int Version, string EventData) : IEventEnvelope
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string? TenantId => null;
        public long GlobalPosition => 1;
        public EventType EventType => new(Slug, Version);
        public IReadOnlyCollection<EventTag> Tags { get; } = [];
        public IReadOnlyDictionary<string, string> Metadata { get; } = new Dictionary<string, string>();
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    }

    /// <summary><see cref="SampleEvolver"/>'s handlers, dispatched by reflection.</summary>
    private sealed class ReflectedTwin : Evolver<SampleState>,
        IEvolve<SampleState, PlainNote>,
        IEvolve<SampleState, VersionedOrder>
    {
        public SampleState Apply(SampleState state, PlainNote e) => state with { Notes = state.Notes + 1 };

        public SampleState Apply(SampleState state, VersionedOrder e)
            => state with { Total = state.Total + e.Amount, LastCountry = e.Country };
    }

    /// <summary>A table that says something different from the interfaces, so it shows which one ran.</summary>
    private sealed class TableDisagrees : Evolver<SampleState>, IGeneratedEvolver<SampleState>,
        IEvolve<SampleState, PlainNote>,
        IEvolve<SampleState, VersionedOrder>
    {
        public Type GeneratedFor => typeof(TableDisagrees);

        public IReadOnlyList<(Type EventType, Func<SampleState, object, SampleState> Apply)> EvolveTable() =>
            [(typeof(PlainNote), (state, _) => state with { Notes = 100 })];

        public SampleState Apply(SampleState state, PlainNote e) => state with { Notes = -1 };

        public SampleState Apply(SampleState state, VersionedOrder e) => state with { Total = -1 };
    }

    [EventType("generated-evolver-extra")]
    private sealed record ExtraNote(string Text) : IEvent;

    /// <summary>Inherits the generated table, which does not know about <see cref="ExtraNote"/>.</summary>
    private sealed class DerivedFromGenerated : SampleEvolver, IEvolve<SampleState, ExtraNote>
    {
        public SampleState Apply(SampleState state, ExtraNote e) => state with { LastCountry = "extra" };
    }
}
