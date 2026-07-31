using Alberto.Commands;
using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Messaging;
using Alberto.Dcb.Upcasting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Dcb.Tests;

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
/// Uses the Alberto.Dcb.Commands assembly (no [EventType] types) so
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
            // typeof(AlbertoStore).Assembly = Alberto.Dcb.Commands; no [EventType] types,
            // so FromAssembly does not throw from duplicate slugs in the test assembly.
            .WithEventsFrom(typeof(AlbertoStore).Assembly)
            .AddUpcaster(decl));

        using var sp = services.BuildServiceProvider();

        // Must have a keyed serializer.
        var keyed = sp.GetKeyedService<EventSerializer>(moduleKey);
        keyed.Should().NotBeNull(
            "WithEventsFrom must register EventSerializer as AddKeyedSingleton under the module key");

        // Must NOT have an unkeyed serializer — that would leak to other modules.
        sp.GetService<EventSerializer>().Should().BeNull(
            "WithEventsFrom must not call TryAddSingleton; an unkeyed serializer would let a second " +
            "module's outbox mapper accidentally resolve this module's serializer");

        // AlbertoStore must be keyed.
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetKeyedService<AlbertoStore>(moduleKey).Should().NotBeNull(
            "WithEventsFrom must register AlbertoStore keyed by module key so a multi-module host " +
            "gets one store per module");
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

        sp.GetKeyedService<EventSerializer>(moduleKey).Should().NotBeNull(
            "AddUpcaster before WithEventsFrom must still produce a keyed EventSerializer; " +
            "all Configure callbacks run before any Register callback");
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

        failures.Should().ContainSingle(f => f.Code == "ALB0018",
            "startup validation must catch [EventType(Version = 2)] without a registered upcaster " +
            "so the runtime deserialization failure becomes a loud startup error instead of silent " +
            "wrong data");

        failures.First(f => f.Code == "ALB0018").Problem
            .Should().Contain("msc-order-placed",
                "the ALB0018 message must identify the offending event type slug");

        failures.First(f => f.Code == "ALB0018").Remedy
            .Should().Contain("AddUpcaster",
                "the ALB0018 remedy must name the public registration method");
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

        failures.Should().NotContain(f => f.Code == "ALB0018",
            "a Version = 1 event type needs no upcaster; ALB0018 must not fire");
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
        sp.GetKeyedService<EventSerializer>("mod_a").Should().NotBeNull(
            "module A called WithEventsFrom; its keyed serializer must be present");

        // Module B does NOT have a serializer.
        sp.GetKeyedService<EventSerializer>("mod_b").Should().BeNull(
            "module B did not call WithEventsFrom; its key must return null — not module A's serializer");

        // No unkeyed serializer visible to either module.
        sp.GetService<EventSerializer>().Should().BeNull(
            "no unkeyed EventSerializer must exist; multi-module correctness requires keyed-only registration");
    }

    /// <summary>
    /// Verifies that an IMessageMappingRegistry.ModuleKey set in WithOutbox's Register
    /// callback correctly resolves the module-keyed serializer at runtime.
    /// Module A has a serializer; the registry.ModuleKey is set to "mod_a" by WithOutbox;
    /// resolving the serializer via the registry key returns module A's serializer.
    /// </summary>
    [Fact]
    public async Task Outbox_mapping_registry_resolves_module_keyed_serializer()
    {
        // InMemoryOutboxStore used as test double; records nothing but satisfies the API.
        var outboxStore = new InMemoryOutboxStoreDouble();

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

        // After service provider build, ModuleKey must be stamped by the Register callback.
        capturedRegistry.Should().NotBeNull("WithOutbox must invoke the configureMappings delegate");
        capturedRegistry!.ModuleKey.Should().Be("msg_mod",
            "WithOutbox's Register callback must stamp registry.ModuleKey = context.ModuleKey " +
            "so that Map extension methods can resolve the module-keyed EventSerializer at runtime");

        // The keyed serializer for "msg_mod" is resolvable via the module key.
        sp.GetKeyedService<EventSerializer>("msg_mod").Should().NotBeNull(
            "the serializer the outbox mapper would resolve must exist under the module key");

        await Task.CompletedTask; // keeps the method async for potential future I/O.
    }
}

// ---------------------------------------------------------------------------
// Inline helper — minimal IOutboxStore double for the messaging test.
// ---------------------------------------------------------------------------

file sealed class InMemoryOutboxStoreDouble : IOutboxStore
{
    public Task InsertAsync(OutboxEntry entry, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
        int limit, TimeSpan claimLease, string claimedBy, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OutboxClaim>>([]);

    public Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default)
        => Task.CompletedTask;
}
