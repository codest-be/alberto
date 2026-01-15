namespace Alberto.Dcb.Admin.Api.Models;

/// <summary>
/// Result of a dead letter retry operation.
/// </summary>
public sealed record DeadLetterRetryResult(
    Guid Id,
    bool Success,
    string? ErrorMessage);
