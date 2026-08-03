using System.Text.Json;
using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// Types at namespace scope so ProcessorId.For<T>() returns the simple type name,
// avoiding a "DeclaringType.TypeName" prefix that would break id derivation.
namespace Alberto.Tests.Subscriptions;

[EventType("react-reg-event")]
internal sealed record ReactRegEvent(string Id) : IEvent;

/// <summary>
/// Typed handler for the no-context ReactTo overload (Overload 3:
/// <c>Func&lt;THandler, Func&lt;TEvent, CancellationToken, Task&gt;&gt;</c>).
/// </summary>
internal sealed class SimpleReactHandler
{
    public Task HandleAsync(ReactRegEvent e, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Typed handler for the ReactorContext overload (Overload 4:
/// <c>Func&lt;THandler, Func&lt;TEvent, ReactorContext, CancellationToken, Task&gt;&gt;</c>).
/// </summary>
internal sealed class ContextReactHandler
{
    public Task HandleAsync(ReactRegEvent e, ReactorContext ctx, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>
/// Handler whose <see cref="ProcessorIdAttribute"/> derives a reserved processor id
/// (<c>"consumer"</c>).  Used to verify that <c>ValidateProcessorIdArg</c> names the handler
/// type in its error message when the id was derived from the type rather than passed explicitly
/// (L890 of <c>DcbModuleBuilderExtensions.cs</c>).
/// </summary>
[ProcessorId("consumer")]
internal sealed class HandlerWithReservedProcessorId
{
    public Task HandleAsync(ReactRegEvent e, ReactorContext ctx, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>
/// Covers the sync/async registration branches of all four <c>ReactTo</c> overloads in
/// <c>DcbModuleBuilderExtensions.cs</c>, concentrating on the paths that were
/// mutation-test gaps:
/// <list type="bullet">
///   <item>L201-211  factory, no context, Sync → <see cref="IPostAppendHandler"/></item>
///   <item>L280-288  factory, with context, Sync → <see cref="IPostAppendHandler"/></item>
///   <item>L367-383  typed handler, no context, Sync → <see cref="IPostAppendHandler"/></item>
///   <item>L428-498  typed handler, with context — both Sync and Async branches</item>
///   <item>L779      <c>ValidateSyncExecutionOptions</c> throw via the context overload</item>
///   <item>L890      <c>ValidateProcessorIdArg</c> names the handler type in the error</item>
/// </list>
/// </summary>
public sealed class ReactToRegistrationTests
{
    private const string ModuleKey = "orders";

    private static IServiceProvider BuildModule(Action<DcbModuleBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, m => { m.WithInMemory(); configure(m); });
        return services.BuildServiceProvider();
    }

    private static IReadOnlyList<IPostAppendHandler> SyncHandlers(IServiceProvider sp) =>
        sp.GetKeyedServices<IPostAppendHandler>(ModuleKey).ToList();

    private static IReadOnlyList<IEventProcessor> AsyncProcessors(IServiceProvider sp) =>
        sp.GetKeyedServices<IEventProcessor>(ModuleKey).ToList();

    // ── Overload 1: factory(IServiceProvider) → Func<TEvent, CancellationToken, Task> ───────

    [Fact]
    public void ReactTo_factory_no_context_sync_registers_IPostAppendHandler()
    {
        var sp = BuildModule(m => m.ReactTo<ReactRegEvent>(
            _ => (e, ct) => Task.CompletedTask,
            "reactor_a",
            ReactorMode.Sync));

        SyncHandlers(sp).Should().ContainSingle(
            "a Sync reactor must register IPostAppendHandler so it runs during AppendAsync");
        AsyncProcessors(sp).Should().BeEmpty(
            "sync mode must not register an IEventProcessor");
    }

    // ── Overload 2: factory(IServiceProvider) → Func<TEvent, ReactorContext, CancellationToken, Task>

    [Fact]
    public void ReactTo_factory_with_context_sync_registers_IPostAppendHandler()
    {
        var sp = BuildModule(m => m.ReactTo<ReactRegEvent>(
            _ => (e, ctx, ct) => Task.CompletedTask,
            "reactor_b",
            ReactorMode.Sync));

        SyncHandlers(sp).Should().ContainSingle(
            "a Sync reactor must register IPostAppendHandler so it runs during AppendAsync");
        AsyncProcessors(sp).Should().BeEmpty(
            "sync mode must not register an IEventProcessor");
    }

    [Fact]
    public void ReactTo_factory_with_context_async_registers_IEventProcessor()
    {
        var sp = BuildModule(m => m.ReactTo<ReactRegEvent>(
            _ => (e, ctx, ct) => Task.CompletedTask,
            "reactor_c")); // default: ReactorMode.Async

        AsyncProcessors(sp).Should().ContainSingle(
            "an Async reactor must register IEventProcessor for background polling");
        SyncHandlers(sp).Should().BeEmpty(
            "async mode must not register IPostAppendHandler");
    }

    // ── Overload 3: typed handler, no context (Func<TEvent, CancellationToken, Task>) ────────

    [Fact]
    public void ReactTo_typed_handler_no_context_sync_registers_IPostAppendHandler()
    {
        var sp = BuildModule(m => m.ReactTo<ReactRegEvent, SimpleReactHandler>(
            h => h.HandleAsync,
            mode: ReactorMode.Sync));

        SyncHandlers(sp).Should().ContainSingle(
            "a Sync reactor must register IPostAppendHandler so it runs during AppendAsync");
        AsyncProcessors(sp).Should().BeEmpty(
            "sync mode must not register an IEventProcessor");
    }

    // ── Overload 4 (L428-498): typed handler, with ReactorContext ────────────────────────────

    [Fact]
    public void ReactTo_typed_handler_with_context_async_registers_IEventProcessor()
    {
        var sp = BuildModule(m => m.ReactTo<ReactRegEvent, ContextReactHandler>(
            h => h.HandleAsync)); // default: ReactorMode.Async

        AsyncProcessors(sp).Should().ContainSingle(
            "an Async reactor must register IEventProcessor for background polling");
        SyncHandlers(sp).Should().BeEmpty(
            "async mode must not register IPostAppendHandler");
    }

    [Fact]
    public void ReactTo_typed_handler_with_context_sync_registers_IPostAppendHandler()
    {
        var sp = BuildModule(m => m.ReactTo<ReactRegEvent, ContextReactHandler>(
            h => h.HandleAsync,
            mode: ReactorMode.Sync));

        SyncHandlers(sp).Should().ContainSingle(
            "a Sync reactor must register IPostAppendHandler so it runs during AppendAsync");
        AsyncProcessors(sp).Should().BeEmpty(
            "sync mode must not register an IEventProcessor");
    }

    // ── L779: ValidateSyncExecutionOptions throw (via typed-context overload) ─────────────────

    [Fact]
    public void ReactTo_typed_handler_with_context_sync_with_batching_enabled_throws()
    {
        var act = () => BuildModule(m => m.ReactTo<ReactRegEvent, ContextReactHandler>(
            h => h.HandleAsync,
            mode: ReactorMode.Sync,
            configure: o => o with { BatchingMode = ProcessorBatchingMode.IfSupported }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Sync*batching*",
                "a Sync reactor cannot enable async batching");
    }

    // ── L890: ValidateProcessorIdArg names the handler type in the error (context overload) ──

    [Fact]
    public void ReactTo_typed_context_handler_with_reserved_derived_id_names_handler_type_in_error()
    {
        // HandlerWithReservedProcessorId carries [ProcessorId("consumer")].
        // Calling without an explicit processorId → derived id is "consumer" (reserved), and
        // ValidateProcessorIdArg must name the handler type so the user knows which class to
        // annotate with a valid [ProcessorId] value.
        var act = () => BuildModule(m => m.ReactTo<ReactRegEvent, HandlerWithReservedProcessorId>(
            h => h.HandleAsync));

        act.Should().Throw<ArgumentException>()
            .WithParameterName("processorId")
            .And.Message.Should().Contain(nameof(HandlerWithReservedProcessorId),
                "the error must name the handler type so the user can fix the [ProcessorId] attribute");
    }

    // ── End-to-end: sync reactor is invoked during AppendAsync ───────────────────────────────

    [Fact]
    public async Task Sync_reactor_factory_no_context_is_invoked_during_AppendAsync()
    {
        var invoked = 0;

        var sp = BuildModule(m => m.ReactTo<ReactRegEvent>(
            _ => (e, ct) => { Interlocked.Increment(ref invoked); return Task.CompletedTask; },
            "e2e_reactor",
            ReactorMode.Sync));

        var store = sp.GetRequiredKeyedService<IEventStore>(ModuleKey);
        var evt = CreateEvent(new ReactRegEvent("e2e"));

        await store.AppendAsync([evt], cancellationToken: TestContext.Current.CancellationToken);

        invoked.Should().Be(1, "the sync reactor must be called inline during AppendAsync");
    }

    private static EventToPersist CreateEvent<TEvent>(TEvent payload) where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventToPersist
        {
            EventType = new EventType(eventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(payload),
        };
    }
}
