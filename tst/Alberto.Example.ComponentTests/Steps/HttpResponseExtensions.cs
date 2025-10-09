using System.Net.Http.Json;
using System.Text.Json;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;

namespace Alberto.Example.ComponentTests.Steps;

public static class HttpResponseExtensions
{
    public static async Task<Result<T>> ToResult<T>(this HttpResponseMessage response, CancellationToken ct = default)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(ct);
            return value != null ? Result<T>.Success(value) : Result<T>.Fail("Null response value");
        }

        // Parse problem details for error
        var json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Extract error code from "title" field (which contains the error code)
            var errorCode = root.TryGetProperty("title", out var title) ? title.GetString() : "UNKNOWN_ERROR";
            var detail = root.TryGetProperty("detail", out var detailProp) ? detailProp.GetString() : json;

            return Result<T>.Fail(Problem.Create(errorCode ?? "UNKNOWN_ERROR", detail ?? ""));
        }
        catch
        {
            return Result<T>.Fail($"HTTP {response.StatusCode}: {json}");
        }
    }
}