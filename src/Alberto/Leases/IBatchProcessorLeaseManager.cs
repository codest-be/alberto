namespace Alberto.Leases;

/// <summary>
/// Acquires many processor leases in one round trip. Internal and optional, like
/// <see cref="IRebalancingLeaseManager"/>: a manager without it is called once per processor.
/// </summary>
internal interface IBatchProcessorLeaseManager
{
    /// <summary>
    /// Same rules as <see cref="IProcessorLeaseManager.TryAcquireAsync"/> for each id: a lease is
    /// won when it is free, expired or already this replica's, and every win draws a new fence
    /// token. A processor another live replica holds is not won and draws no token.
    /// </summary>
    /// <returns>The leases won; ids not in the result were not acquired.</returns>
    Task<IReadOnlyList<IProcessorLease>> TryAcquireManyAsync(
        string consumerId, IReadOnlyCollection<string> processorIds, string replicaId,
        CancellationToken cancellationToken);
}
