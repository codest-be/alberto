using Alberto.ComponentTests;

namespace Alberto.Example.ComponentTests;

public static class ScenarioContextExtensions
{
    public static void StoreOrderId(this ScenarioContext scenarioContext, Guid orderId)
    {
        scenarioContext.Set(orderId, "order");
    }

    public static Guid? TryGetOrderId(this ScenarioContext scenarioContext)
        => scenarioContext.TryGet<Guid>("order");

    public static Guid GetOrderId(this ScenarioContext scenarioContext)
        => scenarioContext.TryGetOrderId() ??
           throw new InvalidOperationException("Order not found in scenario context");


    public static void StoreResponse(this ScenarioContext scenarioContext, HttpResponseMessage response)
    {
        scenarioContext.Set(response, "response");
    }

    public static HttpResponseMessage? TryGetResponse(this ScenarioContext scenarioContext)
        => scenarioContext.TryGet<HttpResponseMessage>("response");

    public static HttpResponseMessage GetResponse(this ScenarioContext scenarioContext)
        => scenarioContext.TryGetResponse() ??
           throw new InvalidOperationException("Response not found in scenario context");

    public static void StoreTenantId(this ScenarioContext scenarioContext, string tenantId)
    {
        scenarioContext.Set(tenantId, "tenant");
    }

    public static string GetTenantId(this ScenarioContext scenarioContext)
        => scenarioContext.TryGet<string>("tenant") ?? "default";

    public static HttpClient HttpClientWithTenant(this ScenarioContext scenarioContext)
    {
        var client = scenarioContext.HttpClient();
        var tenantId = scenarioContext.GetTenantId();
        client.DefaultRequestHeaders.Add("X-Tenant", tenantId);
        return client;
    }
}