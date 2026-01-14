namespace Alberto.Dcb.Admin.Api.Models;

/// <summary>
/// Paginated result wrapper.
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);
