using Microsoft.Extensions.DependencyInjection;

namespace Alberto.ComponentTests.Steps;

public class EventIsConsumed<T> : IStep where T : class
{
    private Func<ScenarioContext, T, bool> _predicate = (_, _) => true;

    public ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        var collector = scenarioContext.GetRequiredService<SubscriptionEventCollector>();

        collector.WaitForEvent<T>(e => _predicate(scenarioContext, e), TimeSpan.FromSeconds(3));
        return ValueTask.CompletedTask;
    }

    public EventIsConsumed<T> WithPredicate(Func<ScenarioContext, T, bool> predicate)
    {
        _predicate = predicate;
        return this;
    }
}