namespace Alberto.ComponentTests;

public static class ScenarioContextExtensions
{
    public static void StoreResponse(this ScenarioContext scenarioContext, HttpResponseMessage response)
    {
        scenarioContext.Set(response, "response");
    }

    public static HttpResponseMessage? TryGetResponse(this ScenarioContext scenarioContext)
        => scenarioContext.TryGet<HttpResponseMessage>("response");

    public static HttpResponseMessage GetResponse(this ScenarioContext scenarioContext)
        => scenarioContext.TryGetResponse() ??
           throw new InvalidOperationException("Response not found in scenario context");
}