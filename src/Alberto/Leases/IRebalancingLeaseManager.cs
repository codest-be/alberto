namespace Alberto.Leases;

/// <summary>
/// What <see cref="Configuration.ProcessorLeaseOptions.Rebalance"/> needs from a lease manager on
/// top of <see cref="IProcessorLeaseManager"/>. Internal so the shipped interface stays as it is;
/// the Postgres and in-memory managers implement it.
/// </summary>
internal interface IRebalancingLeaseManager
{
    /// <summary>Distinct replicas holding an unexpired lease (presence rows included) in the group.</summary>
    Task<int> CountLiveReplicasAsync(string consumerId, CancellationToken cancellationToken);

    /// <summary>
    /// Renews only the named leases of the replica. Renewing every row instead would bring back the
    /// leases the replica is shedding, and they would never reach another replica.
    /// </summary>
    Task<IReadOnlyList<string>> RenewLeasesAsync(
        string consumerId, string replicaId, IReadOnlyCollection<string> processorIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the named leases while this replica still holds them, so a shed processor is free
    /// at once instead of after expiry. A row another replica has taken since is left alone.
    /// </summary>
    Task ReleaseLeasesAsync(
        string consumerId, string replicaId, IReadOnlyCollection<string> processorIds,
        CancellationToken cancellationToken);
}
