using Alberto.Commands;
using System.Text.Json;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Tests.SampleEvents;
using Alberto.Dcb.Upcasting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Dcb.Tests;

// ---------------------------------------------------------------------------
// The regression suite for the gap that let upcasting ship unusable.
//
// Every other upcaster test constructs an EventSerializer by hand, so all of
// them stayed green while WithEventsFrom built an upcaster-free serializer and
// no application could switch the feature on. The plumbing downstream was
// correct; the chain reaching it was always empty.
//
// These tests are deliberately written the way a user writes configuration —
// through AddAlberto and the builder chain — and resolve the serializer out of
// the container rather than building one. A test here fails if the registration
// path ever stops threading the upcaster chain into DI, no matter how correct
// the consumer plumbing is.
//
// They use the separate Alberto.Dcb.Tests.SampleEvents assembly because
// EventSerializer.FromAssembly throws on duplicate slugs and this test assembly
// collides with itself.
// ---------------------------------------------------------------------------

public class UpcasterConfigurationPathTests
{
    private const string ModuleKey = "cfg_path";

    /// <summary>The step a user writes: v1 had no Country, so supply one when reading old events.</summary>
    private static UpcasterDeclaration OrderUpcaster() =>
        DeclareUpcaster.For<VersionedOrder>("e2e-versioned-order")
            .From<VersionedOrderV1>(1, v1 => new VersionedOrder(v1.OrderId, v1.Amount, "unknown"))
            .Build();

    private static ServiceProvider BuildHost()
    {
        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, builder => builder
            .WithInMemory()
            .WithEventsFrom(typeof(VersionedOrder).Assembly)
            .AddUpcaster(OrderUpcaster()));
        return services.BuildServiceProvider();
    }

    /// <summary>A stored v1 event: JSON with no Country, and an event type at version 1.</summary>
    private static IEventEnvelope StoredV1Event(Guid orderId) =>
        new StoredEvent(
            "e2e-versioned-order",
            1,
            JsonSerializer.Serialize(new { OrderId = orderId, Amount = 42.5m }));

    [Fact]
    public void WithEventsFrom_AddUpcaster_registers_a_serializer_that_actually_upcasts()
    {
        using var sp = BuildHost();
        var orderId = Guid.NewGuid();

        // Resolved from DI, not constructed — this is the assertion four waves of tests missed.
        var serializer = sp.GetKeyedService<EventSerializer>(ModuleKey);
        serializer.Should().NotBeNull("WithEventsFrom must register the module's serializer");

        var evt = serializer!.Deserialize(StoredV1Event(orderId));

        evt.Should().BeOfType<VersionedOrder>();
        var order = (VersionedOrder)evt;
        order.OrderId.Should().Be(orderId);
        order.Amount.Should().Be(42.5m);
        order.Country.Should().Be(
            "unknown",
            "the upcaster registered through AddUpcaster must run — anything else here means the "
            + "configuration path built an upcaster-free serializer, which is exactly the defect "
            + "that survived four rounds of green tests");
    }

    [Fact]
    public void Current_version_events_pass_through_without_upcasting()
    {
        using var sp = BuildHost();
        var orderId = Guid.NewGuid();
        var serializer = sp.GetRequiredKeyedService<EventSerializer>(ModuleKey);

        var stored = new StoredEvent(
            "e2e-versioned-order",
            2,
            JsonSerializer.Serialize(new { OrderId = orderId, Amount = 10m, Country = "BE" }));

        var order = (VersionedOrder)serializer.Deserialize(stored);

        order.Country.Should().Be("BE", "a v2 event must not be routed through the v1 step");
    }

    [Fact]
    public void Unversioned_events_in_the_same_assembly_still_deserialize()
    {
        // The registry under test is not degenerate: assembly scanning has to cope with a mix of
        // versioned and unversioned types, and the upcaster must not intercept the wrong slug.
        using var sp = BuildHost();
        var noteId = Guid.NewGuid();
        var serializer = sp.GetRequiredKeyedService<EventSerializer>(ModuleKey);

        var note = (PlainNote)serializer.Deserialize(new StoredEvent(
            "e2e-plain-note",
            1,
            JsonSerializer.Serialize(new { NoteId = noteId, Text = "hello" })));

        note.NoteId.Should().Be(noteId);
        note.Text.Should().Be("hello");
    }

    [Fact]
    public void Builder_call_order_does_not_change_the_result()
    {
        // .AddUpcaster before .WithEventsFrom must behave identically. The serializer is built in a
        // Register callback, after every Configure callback has run, so neither ordering may win.
        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, builder => builder
            .WithInMemory()
            .AddUpcaster(OrderUpcaster())
            .WithEventsFrom(typeof(VersionedOrder).Assembly));

        using var sp = services.BuildServiceProvider();
        var serializer = sp.GetRequiredKeyedService<EventSerializer>(ModuleKey);

        var order = (VersionedOrder)serializer.Deserialize(StoredV1Event(Guid.NewGuid()));

        order.Country.Should().Be("unknown");
    }

    [Fact]
    public void Forgetting_the_upcaster_fails_validation_rather_than_writing_nulls()
    {
        // The misconfiguration the feature exists to catch: bump the version, forget the upcaster.
        // Asserted through the real options pipeline rather than by calling the validator directly,
        // so it also proves WithEventsFrom populates RegisteredEventTypes from the scanned assembly.
        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, builder => builder
            .WithInMemory()
            .WithEventsFrom(typeof(VersionedOrder).Assembly));

        using var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(ModuleKey);

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainMatch("*ALB0018*",
                "a Version = 2 event type with no upcaster must be rejected at startup");
    }

    [Fact]
    public void An_upcaster_for_a_slug_the_assembly_does_not_contain_fails_validation()
    {
        // ALB0019, reached the same way a user would: a typo in the slug string passed to
        // DeclareUpcaster.For. The declaration compiles, and the chain is never consulted because
        // nothing is ever stored under that slug — the upcaster is simply dead. Only a cross-check
        // against the scanned assembly can catch it, which is why this belongs on the DI path.
        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, builder => builder
            .WithInMemory()
            .WithEventsFrom(typeof(VersionedOrder).Assembly)
            .AddUpcaster(OrderUpcaster())
            .AddUpcaster(DeclareUpcaster.For<VersionedOrder>("e2e-versionedorder")   // typo
                .From<VersionedOrderV1>(1, v1 => new VersionedOrder(v1.OrderId, v1.Amount, "x"))
                .Build()));

        using var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(ModuleKey);

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainMatch("*ALB0019*");
    }

    /// <summary>An event as it comes back off the log: a slug, a version, and JSON.</summary>
    private sealed record StoredEvent(string Slug, int Version, string Json) : IEventEnvelope
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string? TenantId => null;
        public long GlobalPosition => 1;
        public EventType EventType => new(Slug, Version);
        public IReadOnlyCollection<EventTag> Tags { get; } = [];
        public string EventData => Json;
        public IReadOnlyDictionary<string, string> Metadata { get; } = new Dictionary<string, string>();
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    }
}
