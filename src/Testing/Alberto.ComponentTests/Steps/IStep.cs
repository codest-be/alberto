namespace Alberto.ComponentTests.Steps;

public interface IStep
{
    ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default);
}