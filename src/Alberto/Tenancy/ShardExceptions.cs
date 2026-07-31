using System.Diagnostics.CodeAnalysis;

namespace Alberto.Tenancy;

/// <summary>
/// Thrown when a tenant has no shard assignment and the module declares no default shard.
/// </summary>
/// <remarks>
/// This is the deliberate strict mode: without a default shard, Alberto will not guess where a
/// tenant belongs, because guessing wrong writes events into a database the tenant will never be
/// read from again. Assign the tenant with <c>alberto shards assign</c>, or declare a default
/// shard with <c>.WithDefaultShard(...)</c> to have new tenants placed automatically.
/// </remarks>
[Experimental("ALB9001")]
public sealed class UnknownTenantException(string moduleKey, string tenantId)
    : InvalidOperationException(
        $"Tenant '{tenantId}' has no shard assignment in module '{moduleKey}', and the module " +
        "declares no default shard. Assign it with 'alberto shards assign " +
        $"{tenantId} --shard <id>', or declare a default shard with .WithDefaultShard(\"...\").")
{
    /// <summary>The module the tenant was being resolved for.</summary>
    public string ModuleKey { get; } = moduleKey;

    /// <summary>The tenant with no assignment.</summary>
    public string TenantId { get; } = tenantId;
}

/// <summary>
/// Thrown when a tenant's shard cannot serve the request: it is not declared by this deployment,
/// or it is currently unreachable.
/// </summary>
/// <remarks>
/// A shard that is down fails only the tenants assigned to it. Every other shard keeps serving,
/// which is the whole point of splitting them — so this exception is scoped to one request and
/// is not a reason to take the process down.
/// </remarks>
[Experimental("ALB9001")]
public sealed class ShardUnavailableException(string moduleKey, string shardId, string reason, Exception? innerException = null)
    : InvalidOperationException(
        $"Shard '{shardId}' of module '{moduleKey}' is unavailable: {reason}", innerException)
{
    /// <summary>The module the shard belongs to.</summary>
    public string ModuleKey { get; } = moduleKey;

    /// <summary>The shard that could not serve the request.</summary>
    public string ShardId { get; } = shardId;
}
