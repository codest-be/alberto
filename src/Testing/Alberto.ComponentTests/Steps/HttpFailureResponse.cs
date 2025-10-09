using System.Net.Http.Json;
using Xunit;

namespace Alberto.ComponentTests.Steps;

public class HttpFailureResponse(string expectedErrorCode) : IStep
{
    public async ValueTask Execute(ScenarioContext scenarioContext, CancellationToken ct = default)
    {
        var response = scenarioContext.GetResponse();
        Assert.False(response.IsSuccessStatusCode, "Expected request to fail but it succeeded");

        var problemDetails = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken: ct);
        Assert.NotNull(problemDetails);
        Assert.Equal(expectedErrorCode, problemDetails.Title);
    }
}

public record ProblemDetails(string Type, string Title);