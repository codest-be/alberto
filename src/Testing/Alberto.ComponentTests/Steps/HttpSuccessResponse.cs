using Xunit;

namespace Alberto.ComponentTests.Steps;

public class HttpSuccessResponse : IStep
{
    public ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        var response = scenarioContext.GetResponse();
        Assert.True(response.IsSuccessStatusCode);
        return ValueTask.CompletedTask;
    }
}