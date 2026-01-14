namespace Alberto.Dcb.Admin.Api.Models;

/// <summary>
/// Projection state document information.
/// </summary>
public sealed record ProjectionStateDto(
    string TenantId,
    string ProjectionType,
    string DocumentId,
    string State,
    DateTimeOffset UpdatedAt);
