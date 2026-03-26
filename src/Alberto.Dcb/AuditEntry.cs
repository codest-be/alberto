namespace Alberto.Dcb;

public record AuditEntry(
    Guid Id,
    string Operator,
    string Action,
    string? ProcessorId,
    Dictionary<string, object?> Details,
    DateTimeOffset CreatedAt);
