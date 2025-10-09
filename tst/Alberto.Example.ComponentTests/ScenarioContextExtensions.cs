using Alberto.ComponentTests;

namespace Alberto.Example.ComponentTests;

public static class ScenarioContextExtensions
{
    public static void StoreOrder(this ScenarioContext scenarioContext, Guid orderId)
    {
        scenarioContext.Set(orderId, "order");
    }

    public static Guid? TryGetOrder(this ScenarioContext scenarioContext)
        => scenarioContext.TryGet<Guid>("order");

    public static Guid GetOrder(this ScenarioContext scenarioContext)
        => scenarioContext.TryGetOrder() ?? throw new InvalidOperationException("Order not found in scenario context");


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