using Alberto.ComponentTests;
using Alberto.ComponentTests.Steps;

namespace Alberto.Example.ComponentTests.Orders.Steps.Subscriptions;

/// <summary>
/// Waits for subscription processing to catch up
/// </summary>
public sealed class WaitForSubscriptions(int milliseconds = 200) : IStep
{
    public async ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        await Task.Delay(milliseconds, ct);
    }
}