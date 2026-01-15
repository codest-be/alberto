namespace Alberto.Dcb.Admin.Api.Models;

/// <summary>
/// Result of a bulk retry operation on dead letters.
/// </summary>
public sealed record BulkRetryResult(
    int TotalAttempted,
    int SuccessCount,
    int FailCount,
    IReadOnlyList<DeadLetterRetryResult> Results);

/// <summary>
/// Result of a bulk operation on items.
/// </summary>
public sealed record BulkOperationResult(
    int TotalCount,
    int SuccessCount,
    int FailCount,
    IReadOnlyList<OperationItemResult> Items);

/// <summary>
/// Result of a single item operation within a bulk operation.
/// </summary>
public sealed record OperationItemResult(
    string Id,
    bool Success,
    string? ErrorMessage);
