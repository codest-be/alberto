using Alberto.Dcb.Messaging;
using Alberto.Dcb.Postgres.Messaging;
using Alberto.Dcb.Testing.Xunit;
using Xunit;

namespace Alberto.Dcb.Tests.Postgres;

/// <summary>
/// Runs <see cref="OutboxStoreSpecification"/> against <see cref="PostgresOutboxStore"/>
/// using the shipped single-tenant schema.
///
/// <para>
/// <see cref="OutboxStoreSpecification.TimeProvider"/> is <see cref="System.TimeProvider.System"/>
/// so the three lease-expiry facts that require a <c>FakeTimeProvider</c> are automatically
/// skipped. Those facts are fully covered by <c>InMemoryOutboxStoreSpecificationTests</c>,
/// and the existing Postgres-specific lease facts in <c>PostgresOutboxStoreTests</c> verify
/// the same behaviour with real-time sleeps.
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
