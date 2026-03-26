namespace Alberto.Dcb;

public interface IAuditStore
{
    Task LogAsync(string @operator, string action, string? processorId,
        Dictionary<string, object?>? details = null, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEntry>> ListAsync(
        string? processorId = null, int limit = 50,
        CancellationToken ct = default);
}
