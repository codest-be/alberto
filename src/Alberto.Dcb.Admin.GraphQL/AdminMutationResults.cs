namespace Alberto.Dcb.Admin.GraphQL;

/// <summary>Result of a checkpoint set/reset mutation.</summary>
public sealed record CheckpointMutationResult(string ProcessorId, bool Success, string? Message = null);

/// <summary>Result of a dead letter clear mutation.</summary>
public sealed record DeadLetterClearResult(int DeletedCount);

/// <summary>Result of a retry-by-rewind mutation.</summary>
public sealed record RetryByRewindMutationResult(string ProcessorId, long? RewindPosition, int DeletedCount);

/// <summary>Result of releasing tenant leases.</summary>
public sealed record TenantLeaseReleaseResult(int ReleasedCount);
