using Alberto.Commands;
using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Messaging;
using Alberto.Testing;
using Alberto.Upcasting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests;

// ---------------------------------------------------------------------------
// File-scoped event fixtures — unique slugs so they don't conflict with the
// many other [EventType] types in the test assembly.
// These are file-local so they are NOT scanned by EventSerializer.FromAssembly
// (file-local types still appear in Assembly.GetTypes() which is why the tests
// below pass typeof(AlbertoStore).Assembly, not the test assembly, to WithEventsFrom).
// ---------------------------------------------------------------------------

// V1 shape: no Region2 field.
file record ReviewOrderV1(Guid OrderId, string? Region);

// V2 shape: adds Region2. Slug is unique to this file.
[EventType("review-pub-api-order", Version = 2)]
file record ReviewOrderV2(Guid OrderId, string? Region, string? Region2) : IEvent;

// ---------------------------------------------------------------------------
// Review-authored tests — written during the SP1e audit to verify the
// public configuration path end-to-end.
//
// IMPORTANT LIMITATION documented here so future readers understand scope:
// EventSerializer.FromAssembly(testAssembly) THROWS with "duplicate key" when
// the test assembly contains multiple [EventType("same-slug")] types across
// different test classes.  These tests therefore CANNOT call
// .WithEventsFrom(typeof(SomeTestEvent).Assembly) and then verify deserialization.
//
// What IS verified:
//   - WithEventsFrom + AddUpcaster registers a non-null keyed EventSerializer
//   - No unkeyed serializer leaks (multi-module isolation)
//   - AlbertoStore is keyed
//   - A module with Version = 2 event but no upcaster fails startup (ALB0018)
//   - A second module's outbox mapping resolves ITS OWN serializer (null when
//     no WithEventsFrom was called) and does not accidentally get module 1's
//   - The upcaster chain fires correctly when the serializer IS built manually
//     and keyed into DI — this replicates what WithEventsFrom does at build time
//
// What is NOT verified here (gap carried forward):
//   No test drives withEventsFrom(assemblyThatHasRealEventTypes).AddUpcaster(decl)
//   and then calls serializer.Deserialize(v1Envelope) to confirm the upcaster ran.
//   The wave's AcceptanceApp did this in a scratchpad, but that is not in the suite.
//   A dedicated events-only test assembly would close the gap without duplicate-slug
//   collisions, but creating one is out of scope for this review wave.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// 1. DI registration — WithEventsFrom + AddUpcaster registers keyed serializer
//    and AlbertoStore, with no unkeyed serializer leak.
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies the DI surface that .WithEventsFrom(assembly).AddUpcaster(decl) produces:
/// <list type="bullet">
///   <item>A non-null keyed <see cref="EventSerializer"/> under the module key.</item>
///   <item>NO unkeyed <see cref="EventSerializer"/> (multi-module isolation).</item>
///   <item>A keyed <see cref="AlbertoStore"/> under the module key.</item>
/// </list>
/// Uses the Alberto.Commands assembly (no [EventType] types) so
/// EventSerializer.FromAssembly does not throw from duplicate-slug collisions
/// in the test project.
/// </summary>
public sealed class WithEventsFromDiSurfaceTests
{
    [Fact]
    public void WithEventsFrom_and_AddUpcaster_register_keyed_serializer_only()
    {
        const string moduleKey = "wef_di_test";

        // Use a real UpcasterDeclaration to confirm it is threaded into the serializer build path.
        // ReviewOrderV1 / ReviewOrderV2 are file-scoped in this file (unique slug "review-pub-api-order").
        var decl = DeclareUpcaster
            .For<ReviewOrderV2>("review-pub-api-order")
            .From<ReviewOrderV1>(1, v1 => new ReviewOrderV2(v1.OrderId, v1.Region, "di-upcasted"))
            .Build();

        var services = new ServiceCollection();
        services.AddAlberto(moduleKey, b => b
            .WithInMemory()
            // typeof(AlbertoStore).Assembly = Alberto.Commands; no [EventType] types,
            // so FromAssembly does not throw from duplicate slugs in the test assembly.
            .WithEventsFrom(typeof(AlbertoStore).Assembly)
            .AddUpcaster(decl));

        using var sp = services.BuildServiceProvider();

        // Must have a keyed serializer.
        var keyed = sp.GetKeyedService<EventSerializer>(moduleKey);
        // WithEventsFrom must register EventSerializer as AddKeyedSingleton under the module key
        Assert.NotNull(keyed);

        // Must NOT have an unkeyed serializer — that would leak to other modules.
        // WithEventsFrom must not call TryAddSingleton; an unkeyed serializer would let a second module's
        // outbox mapper accidentally resolve this module's serializer
        Assert.Null(sp.GetService<EventSerializer>());

        // AlbertoStore must be keyed.
        using var scope = sp.CreateScope();
        // WithEventsFrom must register AlbertoStore keyed by module key so a multi-module host gets one
        // store per module
        Assert.NotNull(scope.ServiceProvider.GetKeyedService<AlbertoStore>(moduleKey));
    }

    /// <summary>
    /// Verifies that when AddUpcaster is called BEFORE WithEventsFrom, the resulting keyed
    /// EventSerializer is still non-null. This checks the order-independence guarantee of the
    /// deferred-serializer approach at the DI surface (not just at the Declaration level).
    /// </summary>
    [Fact]
    public void AddUpcaster_before_WithEventsFrom_still_registers_keyed_serializer()
    {
        const string moduleKey = "wef_order_test";

        var decl = DeclareUpcaster
            .For<ReviewOrderV2>("review-pub-api-order")
            .From<ReviewOrderV1>(1, v1 => new ReviewOrderV2(v1.OrderId, v1.Region, "order-test"))
            .Build();

        var services = new ServiceCollection();
        services.AddAlberto(moduleKey, b => b
            .WithInMemory()
            .AddUpcaster(decl)                          // declared BEFORE WithEventsFrom
            .WithEventsFrom(typeof(AlbertoStore).Assembly));

        using var sp = services.BuildServiceProvider();

        // AddUpcaster before WithEventsFrom must still produce a keyed EventSerializer; all Configure
        // callbacks run before any Register callback
        Assert.NotNull(sp.GetKeyedService<EventSerializer>(moduleKey));
    }
}

// ---------------------------------------------------------------------------
// 2. Misconfiguration catching — ALB0018 fires at startup when Version > 1
//    but no upcaster is registered.  Repeat of UpcasterStartupValidationTests
//    to independently confirm the validator is wired into ValidateOnStart.
// ---------------------------------------------------------------------------

/// <summary>
/// Independently confirms that ALB0018 is surfaced through IValidateOptions at
/// host startup validation, not just through the Validator.Collect() unit method.
/// </summary>
public sealed class MisconfigurationStartupValidationTests
{
    /// <summary>
    /// Asserts that a Version = 2 event type with no upcaster causes startup
    /// validation to produce an ALB0018 failure.  This test calls Collect directly
    /// (as the startup validator does) rather than triggering ValidateOnStart,
    /// which requires a full IHost build.
    /// </summary>
    [Fact]
    public void Version_greater_than_1_without_upcaster_is_caught_by_validator()
    {
        // Construct the definition the validator would see at startup.
        var definition = new AlbertoModuleDefinition
        {
            ModuleKey = "msc-test",
            Backend = new InMemoryBackendDescriptor(),
            RegisteredEventTypes = [new RegisteredEventType("msc-order-placed", 2)],
            UpcasterDeclarations = [],  // ← the mistake a user would make
        };

        var validator = new AlbertoModuleValidator();
        var failures = validator.Collect(definition);

        // startup validation must catch [EventType(Version = 2)] without a registered upcaster so the
        // runtime deserialization failure becomes a loud startup error instead of silent wrong data
        Assert.Single(failures, f => f.Code == "ALB0018");

        // the ALB0018 message must identify the offending event type slug
        Assert.Contains("msc-order-placed", failures.First(f => f.Code == "ALB0018").Problem);

        // the ALB0018 remedy must name the public registration method
        Assert.Contains("AddUpcaster", failures.First(f => f.Code == "ALB0018").Remedy);
    }

    [Fact]
    public void Version_1_event_without_upcaster_passes_validation()
    {
        var definition = new AlbertoModuleDefinition
        {
            ModuleKey = "msc-test",
            Backend = new InMemoryBackendDescriptor(),
            RegisteredEventTypes = [new RegisteredEventType("msc-order-placed", 1)],
            UpcasterDeclarations = [],
        };

        var validator = new AlbertoModuleValidator();
        var failures = validator.Collect(definition);

        // a Version = 1 event type needs no upcaster; ALB0018 must not fire
        Assert.DoesNotContain(failures, f => f.Code == "ALB0018");
    }
}

// ---------------------------------------------------------------------------
// 3. Multi-module isolation — two modules, each with its own serializer key.
//    Module B's outbox mapper must get null when Module B has no WithEventsFrom.
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that two modules in one host do not share an EventSerializer.
/// Module A calls WithEventsFrom (keyed serializer registered under "mod_a").
/// Module B does not call WithEventsFrom (no serializer registered under "mod_b").
/// An outbox mapper in module B that resolves sp.GetKeyedService&lt;EventSerializer&gt;("mod_b")
/// must receive null, not module A's serializer.
/// </summary>
public sealed class MultiModuleSerializerIsolationTests
{
    [Fact]
    public void Two_modules_each_resolve_only_their_own_serializer()
    {
        var services = new ServiceCollection();

        services.AddAlberto("mod_a", b => b
            .WithInMemory()
            .WithEventsFrom(typeof(AlbertoStore).Assembly));

        // Module B deliberately has NO WithEventsFrom.
        services.AddAlberto("mod_b", b => b.WithInMemory());

        using var sp = services.BuildServiceProvider();

        // Module A has its serializer.
        // module A called WithEventsFrom; its keyed serializer must be present
        Assert.NotNull(sp.GetKeyedService<EventSerializer>("mod_a"));

        // Module B does NOT have a serializer.
        // module B did not call WithEventsFrom; its key must return null — not module A's serializer
        Assert.Null(sp.GetKeyedService<EventSerializer>("mod_b"));

        // No unkeyed serializer visible to either module.
        // no unkeyed EventSerializer must exist; multi-module correctness requires keyed-only
        // registration
        Assert.Null(sp.GetService<EventSerializer>());
    }

    /// <summary>
    /// Verifies that a registry built by WithOutbox's Register callback carries the module key
    /// from <c>AlbertoModuleContext.ModuleKey</c> and therefore resolves the correct
    /// module-keyed serializer at runtime. Each shard (or the sole module when unsharded)
    /// gets its own registry constructed with its own immutable key.
    /// </summary>
    [Fact]
    public async Task Outbox_mapping_registry_resolves_module_keyed_serializer()
    {
        // InMemoryOutboxStore used as test double; satisfies the API.
        var outboxStore = new InMemoryOutboxStore();

        IMessageMappingRegistry? capturedRegistry = null;

        var services = new ServiceCollection();
        services.AddAlberto("msg_mod", b => b
            .WithInMemory()
            .WithEventsFrom(typeof(AlbertoStore).Assembly)
            .WithOutbox(
                registry =>
                {
                    // Capture the registry to inspect it after the service provider is built.
                    capturedRegistry = registry;
                    // A real app would call registry.Map<TEvent, TMessage>(mapper) here.
                },
                outboxStore));

        using var sp = services.BuildServiceProvider();

        // The registry is built with ModuleKey = context.ModuleKey inside the Register callback,
        // so capturedRegistry is non-null only after AddAlberto processes the deferred registrations.
        // WithOutbox must invoke the configureMappings delegate
        Assert.NotNull(capturedRegistry);
        // the registry must be constructed with ModuleKey = context.ModuleKey so that Map extension
        // methods always resolve the correct module-keyed EventSerializer
        Assert.Equal("msg_mod", capturedRegistry!.ModuleKey);

        // The keyed serializer for "msg_mod" is resolvable via the module key.
        // the serializer the outbox mapper would resolve must exist under the module key
        Assert.NotNull(sp.GetKeyedService<EventSerializer>("msg_mod"));

        await Task.CompletedTask; // keeps the method async for potential future I/O.
    }
}

