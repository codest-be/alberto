namespace Alberto.Messaging;

/// <summary>
/// Records the failed delivery attempts of one claimed batch in one round trip instead of one per
/// entry. Internal and optional: <see cref="OutboxRelay"/> falls back to
/// <see cref="IOutboxStore.MarkAttemptFailedAsync"/> per entry when the store does not implement it.
/// </summary>
internal interface IOutboxFailureBatchStore
{
    /// <summary>
    /// Applies <see cref="IOutboxStore.MarkAttemptFailedAsync"/> to every failure in one statement and
    /// returns the ids it marked; an entry whose claim expired or was superseded is left out.
    /// </summary>
    Task<IReadOnlySet<Guid>> MarkAttemptsFailedAsync(
        IReadOnlyList<OutboxFailure> failures, int maxRetries, CancellationToken cancellationToken);
}

/// <param name="Claim">The claim the failed publish was made under.</param>
/// <param name="Error">The error text to record in <c>last_error</c>.</param>
internal readonly record struct OutboxFailure(OutboxClaim Claim, string Error);
