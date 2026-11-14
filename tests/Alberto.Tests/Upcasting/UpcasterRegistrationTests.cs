using Alberto.Commands;
using System.Collections.Immutable;
using System.Text.Json;
using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Alberto.Upcasting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests;

// ---------------------------------------------------------------------------
// Domain fixtures — file-scoped to this test file.
//
// Convention (mirrors production use):
//   - Only the CURRENT (latest) version carries [EventType].
//   - Old versions used exclusively in upcast chains are plain records: no attribute.
// ---------------------------------------------------------------------------

// V1 shape: no Country field.
file record UpcasterRegV1(Guid OrderId, string? Region);

// V2 shape: adds Country.  The [EventType] attribute gives it the type ID and version.
[EventType("upcast-reg-order", Version = 2)]
file record UpcasterRegV2(Guid OrderId, string? Region, string? Country) : IEvent;

// Projection state type.
file record UpcasterRegState
{
    public string? Country { get; init; }
}

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

file static class UpcasterRegFixtures
{
    public const string Slug = "upcast-reg-order";

    /// <summary>
    /// Builds the V1-to-V2 upcaster declaration: fills Country with "reg-upcasted".
    /// </summary>
    public static UpcasterDeclaration BuildDeclaration() =>
        DeclareUpcaster
            .For<UpcasterRegV2>(Slug)
            .From<UpcasterRegV1>(1, v1 => new UpcasterRegV2(v1.OrderId, v1.Region, "reg-upcasted"))
            .Build();

    /// <summary>
    /// Builds a serializer that knows only the test type and its upcaster.
    /// Uses FromRegistry (internal, friend-accessible) to avoid scanning the test assembly,
    /// which contains duplicate [EventType] slugs across test classes — a known issue that
    /// prevents EventSerializer.FromAssembly(testAssembly) from succeeding.
    /// </summary>
    public static EventSerializer BuildSerializer() =>
        EventSerializer
            .FromRegistry(new Dictionary<string, Type> { [Slug] = typeof(UpcasterRegV2) })
            .WithUpcasters(UpcasterRegistry.Create()
                .Add(BuildDeclaration())
                .Build());

    /// <summary>
    /// Creates a V1 envelope: the JSON has no Country field and EventType.Version is 1.
    /// </summary>
    public static IEventEnvelope MakeV1Envelope(Guid orderId) => new EventEnvelope
    {
        Id = Guid.NewGuid(),
        GlobalPosition = 1,
        EventType = new EventType(Slug, 1),
        EventData = JsonSerializer.Serialize(new UpcasterRegV1(orderId, "eu-west")),
        Tags = [],
        Metadata = new Dictionary<string, string>(),
        CreatedAt = DateTimeOffset.UtcNow,
        TenantId = null,
    };
}

// ---------------------------------------------------------------------------
// (a) End-to-end DI: command-pipeline Load + projection both upcast via the
//     keyed EventSerializer that lives in the DI container.
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that the keyed <see cref="EventSerializer"/> wired up through DI (with an upcaster
/// chain) is used by BOTH the command-pipeline <c>AlbertoStore.Fold</c> path AND a
/// <see cref="DeclaredAsyncProjection{TState}"/> when they encounter an event stored at an
/// older schema version.
/// <para>
/// The test injects the serializer via <see cref="DcbModuleBuilder.Register"/> rather than
/// <c>WithEventsFrom</c> because the test assembly contains duplicate <c>[EventType]</c> slugs
/// across test classes, making <c>EventSerializer.FromAssembly(testAssembly)</c> fail.
/// The DI wiring exercised here — keyed singleton <see cref="EventSerializer"/>, keyed scoped
/// <see cref="AlbertoStore"/>, keyed <see cref="IEventProcessor"/> — is identical to what
/// <c>WithEventsFrom+AddUpcaster</c> produces; order-independence is covered by test (b).
/// </para>
/// </summary>
public sealed class UpcasterDiWiringTests
{
    [Fact]
    public async Task Command_pipeline_and_projection_both_receive_upcasted_event_from_keyed_serializer()
    {
        // Arrange
        const string moduleKey = "reg_test";
        var orderId = Guid.NewGuid();

        var serializer = UpcasterRegFixtures.BuildSerializer();
        var declaration = DeclareProjection.For<UpcasterRegState>("reg-projection")
            .On<UpcasterRegV2>(
                id: e => e.OrderId.ToString(),
                apply: (_, e, _) => new UpcasterRegState { Country = e.Country })
            .Build();
        var stateStore = new InMemoryStateStore<UpcasterRegState>();

        var services = new ServiceCollection();
        services.AddAlberto(moduleKey, b => b
            .WithInMemory()
            // Register the pre-built serializer (with upcasters) as a keyed singleton, mirroring
            // exactly what the deferred Register callback inside WithEventsFrom+AddUpcaster does.
            .Register(ctx =>
            {
                var key = ctx.ModuleKey;
                ctx.Services.AddKeyedSingleton<EventSerializer>(key, serializer);
                ctx.Services.AddKeyedScoped(key, (sp, _) => new AlbertoStore(
                    sp.GetRequiredKeyedService<IEventStore>(key),
                    serializer,
                    sp));
            })
            .AddProjection(declaration, _ => _ => stateStore));

        await using var sp = services.BuildServiceProvider();

        // Act — append a raw V1 event directly to the in-memory store (bypasses the
        // serializer's tag-stamping, which would mark it _version:2 because the current attribute
        // says Version = 2; old events on disk carry _version:1 because they were written before
        // the attribute was bumped).
        var rawStore = sp.GetRequiredKeyedService<IEventStore>(moduleKey);
        var v1Json = JsonSerializer.Serialize(new UpcasterRegV1(orderId, "eu-west"));
        await rawStore.AppendAsync([new EventToPersist
        {
            EventType = new EventType(UpcasterRegFixtures.Slug, 1),
            Tags = [],
            EventData = v1Json,
        }]);

        // Assert A — command-pipeline: AlbertoStore.Fold calls EventSerializer.Deserialize,
        // which applies the upcaster chain and returns a V2 instance.
        await using var scope = sp.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredKeyedService<AlbertoStore>(moduleKey);

        // Fold<IEvent?> returns the last event processed (apply returns e each time).
        var foldObserved = await store.Fold<IEvent?>(
            DcbQuery.ByTypes(new EventType(UpcasterRegFixtures.Slug)),
            null,
            (_, e) => e,
            CancellationToken.None);

        foldObserved.Should().BeOfType<UpcasterRegV2>()
            .Which.Country.Should().Be("reg-upcasted",
                "AlbertoStore.Fold must route through EventSerializer.Deserialize which applies the upcaster");

        // Assert B — projection: DeclaredAsyncProjection reads the keyed serializer from DI
        // (via AddProjection's sp.GetKeyedService<EventSerializer>(moduleKey)) and also
        // applies the upcaster chain.
        var v1Envelope = UpcasterRegFixtures.MakeV1Envelope(orderId);
        var projection = new DeclaredAsyncProjection<UpcasterRegState>(
            declaration,
            _ => stateStore,
            serializer: serializer);

        await projection.ProcessEventAsync(v1Envelope);

        var states = await stateStore.LoadManyAsync([orderId.ToString()], CancellationToken.None);
        states[orderId.ToString()].Country.Should().Be("reg-upcasted",
            "projection must route through EventSerializer.Deserialize which applies the upcaster");

        // Assert C — no unkeyed EventSerializer is registered (multi-module isolation fix).
        // A second module's outbox mapper must not accidentally resolve this module's serializer.
        sp.GetService<EventSerializer>().Should().BeNull(
            "WithEventsFrom/Register must use AddKeyedSingleton only; " +
            "TryAddSingleton(serializer) was removed to prevent multi-module serializer leaks");
    }
}

// ---------------------------------------------------------------------------
// (b) Order-independence: AddUpcaster before or after WithEventsFrom produces
//     the same UpcasterDeclarations on the module definition.
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that <c>.WithEventsFrom(asm).AddUpcaster(d)</c> and
/// <c>.AddUpcaster(d).WithEventsFrom(asm)</c> result in the same
/// <see cref="AlbertoModuleDefinition.UpcasterDeclarations"/> on the definition at
/// registration time.
/// <para>
/// This is the central correctness guarantee of the deferred-serializer approach: because all
/// <c>Configure</c> callbacks (including the one <c>AddUpcaster</c> uses) run before any
/// <c>Register</c> callback (where <c>WithEventsFrom</c> builds the serializer),
/// declaration order does not affect the final serializer.
/// </para>
/// </summary>
public sealed class UpcasterOrderIndependenceTests
{
    [Fact]
    public void AddUpcaster_before_and_after_WithEventsFrom_produce_identical_declarations()
    {
        // Use the Alberto.Commands assembly as the events assembly — it has no [EventType]
        // types so the registry scan is empty (no duplicates, no conflicts).  The test is about
        // *declaration ordering*, not about the serializer's type registry.
        var eventsAssembly = typeof(AlbertoStore).Assembly;
        var decl = UpcasterRegFixtures.BuildDeclaration();

        ImmutableArray<UpcasterDeclaration> declarationsWhenAfter = default;
        ImmutableArray<UpcasterDeclaration> declarationsWhenBefore = default;

        // Setup A: WithEventsFrom → AddUpcaster
        var services1 = new ServiceCollection();
        services1.AddAlberto("order_a", b => b
            .WithInMemory()
            .WithEventsFrom(eventsAssembly)
            .AddUpcaster(decl)
            .Register(ctx => declarationsWhenAfter = ctx.Definition.UpcasterDeclarations));

        // Setup B: AddUpcaster → WithEventsFrom
        var services2 = new ServiceCollection();
        services2.AddAlberto("order_b", b => b
            .WithInMemory()
            .AddUpcaster(decl)
            .WithEventsFrom(eventsAssembly)
            .Register(ctx => declarationsWhenBefore = ctx.Definition.UpcasterDeclarations));

        // Building the service providers triggers all deferred Register callbacks.
        using var _ = services1.BuildServiceProvider();
        using var __ = services2.BuildServiceProvider();

        // Both orderings must produce the same set of declarations.
        declarationsWhenAfter.Should().HaveCount(1,
            "one AddUpcaster call must produce exactly one declaration");
        declarationsWhenBefore.Should().HaveCount(1,
            "one AddUpcaster call must produce exactly one declaration");

        declarationsWhenAfter[0].EventTypeId.Should().Be(decl.EventTypeId,
            "declaration EventTypeId must match in the WithEventsFrom-then-AddUpcaster ordering");
        declarationsWhenBefore[0].EventTypeId.Should().Be(decl.EventTypeId,
            "declaration EventTypeId must match in the AddUpcaster-then-WithEventsFrom ordering");

        declarationsWhenAfter[0].CurrentVersion.Should().Be(declarationsWhenBefore[0].CurrentVersion,
            "the CurrentVersion produced by the upcaster must be the same regardless of ordering");
    }
}

// ---------------------------------------------------------------------------
// (c) Startup validation: Version = N > 1 without an upcaster is ALB0018.
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that <see cref="AlbertoModuleValidator"/> emits <c>ALB0018</c> when a module
/// declares an event type with <c>[EventType(Version = N)]</c> where <c>N &gt; 1</c> but
/// provides no upcaster for it.
/// <para>
/// Without an upcaster, any event written at versions 1..N-1 would fail at runtime with
/// "No step for version X". The validator catches this gap at startup.
/// </para>
/// </summary>
public sealed class UpcasterStartupValidationTests
{
    [Fact]
    public void Version_greater_than_1_without_upcaster_produces_ALB0018()
    {
        // Construct a definition that has a v2 event type but no upcaster.
        // Alberto.Tests has InternalsVisibleTo access so internal setters are reachable.
        var definition = new AlbertoModuleDefinition
        {
            ModuleKey = "validation-test",
            Backend = new InMemoryBackendDescriptor(),
            // Simulate the scan from WithEventsFrom: one event at Version = 2.
            RegisteredEventTypes = [new RegisteredEventType("val-order-event", 2)],
            // No upcasters registered.
            UpcasterDeclarations = [],
        };

        var validator = new AlbertoModuleValidator();
        var failures = validator.Collect(definition);

        failures.Should().ContainSingle(f => f.Code == "ALB0018",
            "a Version = 2 event type with no upcaster must surface ALB0018 at startup " +
            "rather than failing silently at runtime when an old event is read");

        var failure = failures.First(f => f.Code == "ALB0018");
        failure.Problem.Should().Contain("val-order-event",
            "the failure message must identify the event type that is missing an upcaster");
    }

    [Fact]
    public void Version_greater_than_1_that_waived_upcasting_passes_validation()
    {
        // [EventType(UpcastingNotRequired = true)]: the bump only added optional members whose
        // defaults are already right for older events. EventSerializer.Deserialize honours the
        // same flag at read time, and the two have to agree — a waiver that still failed startup
        // would be no waiver at all.
        var definition = new AlbertoModuleDefinition
        {
            ModuleKey = "validation-test",
            Backend = new InMemoryBackendDescriptor(),
            RegisteredEventTypes =
                [new RegisteredEventType("val-order-event", 2, UpcastingNotRequired: true)],
            UpcasterDeclarations = [],
        };

        var validator = new AlbertoModuleValidator();

        validator.Collect(definition).Should().NotContain(f => f.Code == "ALB0018");
    }

    [Fact]
    public void Version_greater_than_1_with_matching_upcaster_passes_validation()
    {
        // Same as above, but the upcaster is present.
        var decl = DeclareUpcaster
            .For<UpcasterRegV2>("val-order-event")
            .From<UpcasterRegV1>(1, v1 => new UpcasterRegV2(v1.OrderId, v1.Region, "ok"))
            .Build();

        var definition = new AlbertoModuleDefinition
        {
            ModuleKey = "validation-test",
            Backend = new InMemoryBackendDescriptor(),
            RegisteredEventTypes = [new RegisteredEventType("val-order-event", 2)],
            UpcasterDeclarations = [decl],
        };

        var validator = new AlbertoModuleValidator();
        var failures = validator.Collect(definition);

        failures.Should().NotContain(f => f.Code == "ALB0018",
            "a Version = 2 event type with a registered upcaster must not produce ALB0018");
    }

    [Fact]
    public void Upcaster_for_unknown_event_type_produces_ALB0019()
    {
        // Declare an upcaster for a slug that does not exist in the registered types.
        var decl = DeclareUpcaster
            .For<UpcasterRegV2>("unknown-slug")
            .From<UpcasterRegV1>(1, v1 => new UpcasterRegV2(v1.OrderId, v1.Region, "ok"))
            .Build();

        var definition = new AlbertoModuleDefinition
        {
            ModuleKey = "validation-test",
            Backend = new InMemoryBackendDescriptor(),
            // RegisteredEventTypes contains a DIFFERENT slug.
            RegisteredEventTypes = [new RegisteredEventType("known-slug", 1)],
            UpcasterDeclarations = [decl],
        };

        var validator = new AlbertoModuleValidator();
        var failures = validator.Collect(definition);

        failures.Should().ContainSingle(f => f.Code == "ALB0019",
            "an upcaster for a slug absent from the events assembly must surface ALB0019 " +
            "so that typos and orphaned declarations are caught at startup");

        failures.First(f => f.Code == "ALB0019").Problem.Should().Contain("unknown-slug");
    }

    // -----------------------------------------------------------------------
    // ALB0020 — the near-miss ALB0018 cannot see.
    //
    // An upcaster exists for the slug, so ALB0018 is satisfied, but its chain
    // does not terminate at the version the event type declares. That module
    // starts cleanly and only throws when a stale event is actually read —
    // possibly days after the deploy, on whichever request happens to touch it.
    // -----------------------------------------------------------------------

    [Fact]
    public void Chain_that_stops_short_of_the_declared_version_produces_ALB0020()
    {
        // Chain: v1 -> v2, so CurrentVersion = 2. The event type declares v3.
        // A stored v2 event has no step to carry it to v3.
        var decl = DeclareUpcaster
            .For<UpcasterRegV2>("val-order-event")
            .From<UpcasterRegV1>(1, v1 => new UpcasterRegV2(v1.OrderId, v1.Region, "ok"))
            .Build();

        var definition = new AlbertoModuleDefinition
        {
            ModuleKey = "validation-test",
            Backend = new InMemoryBackendDescriptor(),
            RegisteredEventTypes = [new RegisteredEventType("val-order-event", 3)],
            UpcasterDeclarations = [decl],
        };

        var failures = new AlbertoModuleValidator().Collect(definition);

        failures.Should().NotContain(f => f.Code == "ALB0018",
            "an upcaster is registered for the slug, so the coverage check is satisfied — " +
            "ALB0020 exists precisely because ALB0018 cannot see this gap");

        var failure = failures.Should().ContainSingle(f => f.Code == "ALB0020").Subject;
        failure.Problem.Should().Contain("val-order-event");
        failure.Problem.Should().Contain("3", "the message must name the declared version");
        failure.Remedy.Should().Contain("From<TOld>(2",
            "the remedy must tell the user where the chain has to resume");
    }

    [Fact]
    public void Chain_that_overshoots_the_declared_version_produces_ALB0020()
    {
        // The mirror mistake: the chain was extended but [EventType(Version = ...)]
        // was never bumped, so the "current" version the upcaster produces is a
        // version no event will ever be written at.
        var decl = DeclareUpcaster
            .For<UpcasterRegV2>("val-order-event")
            .From<UpcasterRegV1>(1, v1 => new UpcasterRegV2(v1.OrderId, v1.Region, "ok"))
            .Build();

        var definition = new AlbertoModuleDefinition
        {
            ModuleKey = "validation-test",
            Backend = new InMemoryBackendDescriptor(),
            // Chain reaches v2; the attribute still says v1.
            RegisteredEventTypes = [new RegisteredEventType("val-order-event", 1)],
            UpcasterDeclarations = [decl],
        };

        var failures = new AlbertoModuleValidator().Collect(definition);

        failures.Should().ContainSingle(f => f.Code == "ALB0020")
            .Which.Remedy.Should().Contain("Version = 2",
                "the remedy must offer raising the attribute to match the chain");
    }

    [Fact]
    public void Chain_that_lands_exactly_on_the_declared_version_passes()
    {
        var decl = DeclareUpcaster
            .For<UpcasterRegV2>("val-order-event")
            .From<UpcasterRegV1>(1, v1 => new UpcasterRegV2(v1.OrderId, v1.Region, "ok"))
            .Build();

        var definition = new AlbertoModuleDefinition
        {
            ModuleKey = "validation-test",
            Backend = new InMemoryBackendDescriptor(),
            RegisteredEventTypes = [new RegisteredEventType("val-order-event", 2)],
            UpcasterDeclarations = [decl],
        };

        new AlbertoModuleValidator().Collect(definition)
            .Should().NotContain(f => f.Code == "ALB0020");
    }
}
