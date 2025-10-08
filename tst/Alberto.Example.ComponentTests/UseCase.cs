using System.Text.Json;
using Alberto.CQRS.Commands;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.EventStore.InMemory;
using Alberto.Example.Modules.Orders;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Example.ComponentTests;

public sealed class UseCase
{
    private readonly InMemoryEventStoreBackend _eventStore;
    private readonly List<Guid> _givenEventIds = [];
    private readonly IServiceCollection _services;
    private IServiceProvider? _serviceProvider;

    internal UseCase(IServiceCollection services, InMemoryEventStoreBackend eventStore)
    {
        _services = services;
        _eventStore = eventStore;
    }

    public UseCase Given(Guid orderId, params object[] events)
    {
        _serviceProvider ??= _services.BuildServiceProvider();

        var eventsToPersist = events.Select(evt => ToEventToPersist(evt, orderId)).ToList();

        using var scope = _serviceProvider.CreateScope();
        var orderEventStore = scope.ServiceProvider.GetRequiredService<OrderEventStore>();

        var streamQuery = new StreamQuery([new EventTag(Tags.Order, orderId.ToString())]);

        var persisted = orderEventStore.Append(eventsToPersist, streamQuery, null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        _givenEventIds.AddRange(persisted.Select(e => e.Id));

        return this;
    }

    public CommandAsserter When<TCommand>(TCommand command) where TCommand : ICommand
    {
        _serviceProvider ??= _services.BuildServiceProvider();

        object? result;

        using (var scope = _serviceProvider.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<CommandExecutor>();
            result = executor.Execute(command, CancellationToken.None).GetAwaiter().GetResult();
        }

        return new CommandAsserter(result!, _givenEventIds.ToArray(), _eventStore);
    }

    public CommandAsserter When<TCommand, TResult>(TCommand command) where TCommand : ICommand
    {
        _serviceProvider ??= _services.BuildServiceProvider();

        object? result;

        using (var scope = _serviceProvider.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<CommandExecutor>();
            result = executor.Execute<TCommand, TResult>(command, CancellationToken.None).GetAwaiter().GetResult();
        }

        return new CommandAsserter(result!, _givenEventIds.ToArray(), _eventStore);
    }

    private static IEventToPersist ToEventToPersist(object evt, Guid orderId)
    {
        var eventType = EventType.GetEventType(evt.GetType());
        if (eventType == null)
        {
            throw new InvalidOperationException(
                $"Event type {evt.GetType().Name} does not have an [EventType] attribute");
        }

        return new EventToPersist
        {
            EventType = eventType,
            EventJson = JsonSerializer.Serialize(evt),
            Tags = [new EventTag(Tags.Order, orderId.ToString())],
            Metadata = new Dictionary<string, string>(),
            Created = DateTimeOffset.UtcNow
        };
    }
}