using Alberto.Messaging;
using Alberto.Messaging.Postgres;
using Alberto.Testing.Xunit;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Runs <see cref="OutboxStoreSpecification"/> against <see cref="PostgresOutboxStore"/>
/// using the shipped single-tenant schema.
///
/// <para>
/// <see cref="OutboxStoreSpecification.TimeProvider"/> is <see cref="System.TimeProvider.System"/>,
/// and it cannot be anything else: <see cref="PostgresOutboxStore"/> stamps
/// <c>claim_expires_at</c> and <c>delivered_at</c> from the database clock (<c>now()</c>), which
/// no injected <c>TimeProvider</c> can move. The three facts that need controllable time
/// therefore skip here. Each is covered twice over — by
/// <c>InMemoryOutboxStoreSpecificationTests</c> on a <c>FakeTimeProvider</c>, and against real
/// Postgres by a fact in <c>PostgresOutboxStoreTests</c> that backdates the row's
/// <c>claim_expires_at</c> (or <c>delivered_at</c>) with SQL rather than waiting for the clock:
/// <list type="bullet">
/// <item><c>ClaimPendingAsync_ExpiredLease_IsReclaimable</c> →
/// <c>ClaimPendingAsync_ExpiredClaim_IsRecoveredWithNewToken</c></item>
/// <item><c>MarkDeliveredAsync_ExpiredLease_ReturnsFalse</c> →
/// <c>Completion_WithExpiredOrSupersededToken_CannotOverwriteClaim</c></item>
/// <item><c>PurgeDeliveredAsync_RemovesOldDeliveredOnly</c> →
/// <c>PurgeDeliveredAsync_ShouldRemoveDeliveredEntriesOlderThanThreshold</c></item>
/// </list>
/// </para>
/// </summary>
public sealed class PostgresOutboxStoreSpecificationTests(PostgresOutboxStoreFixture fixture)
    : OutboxStoreSpecification, IClassFixture<PostgresOutboxStoreFixture>
{
    /// <inheritdoc/>
    protected override TimeProvider TimeProvider => TimeProvider.System;

    /// <inheritdoc/>
    protected override Task<IOutboxStore> CreateStore() =>
        Task.FromResult<IOutboxStore>(new PostgresOutboxStore(fixture.DataSource));
}
