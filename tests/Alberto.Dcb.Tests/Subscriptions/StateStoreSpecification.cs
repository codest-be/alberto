using Alberto.Dcb.EntityFramework;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Testing.Xunit;
using Alberto.Dcb.Tests.EntityFramework;
using Alberto.Dcb.Tests.Infrastructure;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

// ---------------------------------------------------------------------------
// Shared TState for InMemory and Postgres adapters
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal state type used by InMemory and Postgres spec subclasses.
/// Postgres serialises it as JSONB; InMemory stores it by value.
/// </summary>
public sealed record SimpleState
{
    public int Value { get; init; }
}

// ---------------------------------------------------------------------------
// InMemory adapter
// ---------------------------------------------------------------------------

/// <summary>
/// Runs <see cref="StateStoreSpecification{TState}"/> against
/// <see cref="InMemoryStateStore{TState}"/>.
/// </summary>
public sealed class InMemoryStateStoreSpecificationTests : StateStoreSpecification<SimpleState>
{
    /// <summary>
    /// InMemory stores are not composable: each instance owns a private dictionary, so
    /// two store instances never share state regardless of projectionType.
    /// The rebuild-version facts that require a cross-instance read
    /// (<see cref="StateStoreSpecification{TState}.RebuildVersion_DefaultsToVersionOne"/>)
    /// fall back to a weaker assertion for this adapter; version isolation behaviour is
    /// fully covered by the single-instance facts that drive version changes through a
    /// mutable closure.
    /// </summary>
    protected override bool SupportsSharedBackingStore => false;

    protected override Task<IStateStore<SimpleState>> CreateStore(
        string projectionType,
        Func<int>? rebuildVersion = null) =>
        Task.FromResult<IStateStore<SimpleState>>(
            new InMemoryStateStore<SimpleState>(rebuildVersion));

    protected override SimpleState MakeState(int value) => new() { Value = value };
    protected override int ReadValue(SimpleState state) => state.Value;
}

// ---------------------------------------------------------------------------
// Postgres adapter
// ---------------------------------------------------------------------------

/// <summary>
/// Runs <see cref="StateStoreSpecification{TState}"/> against
/// <see cref="PostgresStateStore{TState}"/> (single-tenant schema).
///
/// <para>
/// The tests that previously lived in <c>PostgresStateStoreTests</c> under the name
/// "TenantIsolation_*" were actually testing projectionType isolation: the helper
/// <c>CreateStore&lt;T&gt;(string?)</c> passed its argument as the <c>projectionType</c>
/// constructor parameter (position 2), not as <c>tenantId</c> (position 5). Those facts
/// are now correctly named and covered by
/// <see cref="StateStoreSpecification{TState}.ProjectionType_Isolates_SameDocId"/>.
/// </para>
/// <para>
/// The real multi-tenant mode (constructing <see cref="PostgresStateStore{TState}"/> with
/// a non-null <c>tenantId</c>) is covered by
/// <see cref="MultiTenantPostgresStateStoreSpecificationTests"/>, which runs the same
/// spec facts against the multi-tenant schema and additionally exercises tenant-isolation
/// semantics via <see cref="StateStoreSpecification{TState}.SupportsTenantIsolation"/>.
/// </para>
/// </summary>
public sealed class PostgresStateStoreSpecificationTests(SingleTenantPostgresFixture fixture)
    : StateStoreSpecification<SimpleState>, IClassFixture<SingleTenantPostgresFixture>
{
    protected override Task<IStateStore<SimpleState>> CreateStore(
        string projectionType,
        Func<int>? rebuildVersion = null) =>
        Task.FromResult<IStateStore<SimpleState>>(
            new PostgresStateStore<SimpleState>(
                fixture.DataSource,
                projectionType,
                rebuildVersion: rebuildVersion));

    protected override SimpleState MakeState(int value) => new() { Value = value };
    protected override int ReadValue(SimpleState state) => state.Value;
}

// ---------------------------------------------------------------------------
// EF adapter
// ---------------------------------------------------------------------------

/// <summary>
/// Runs <see cref="StateStoreSpecification{TState}"/> against
/// <see cref="EfStateStore{TEntity,TDbContext}"/> using <see cref="CounterEntity"/>
/// and <see cref="EfTestDbContext"/>.
///
/// <para>
/// <see cref="StateStoreSpecification{TState}.SupportsProjectionTypeIsolation"/> is
/// <c>false</c>: <c>EfStateStore&lt;CounterEntity&gt;</c> always writes to
/// <c>ef_test_counters</c> regardless of the projectionType string the spec seam
/// supplies. Separation between projections is structural (different TEntity types →
/// different tables), not a runtime string discriminator.
/// </para>
/// <para>
/// All spec tests in this class share one table, so every docId includes
/// <see cref="StateStoreSpecification{TState}.TestId"/> to prevent cross-test
/// interference.
/// </para>
/// </summary>
public sealed class EfStateStoreSpecificationTests(EfProjectionTestFixture fixture)
    : StateStoreSpecification<CounterEntity>, IClassFixture<EfProjectionTestFixture>
{
    protected override bool SupportsProjectionTypeIsolation => false;

    protected override Task<IStateStore<CounterEntity>> CreateStore(
        string projectionType,
        Func<int>? rebuildVersion = null) =>
        // projectionType is accepted to satisfy the spec seam but is not forwarded to
        // EfStateStore — it has no such parameter.
        Task.FromResult<IStateStore<CounterEntity>>(
            new EfStateStore<CounterEntity, EfTestDbContext>(
                fixture.CreateFactory(),
                rebuildVersion));

    protected override CounterEntity MakeState(int value) => new() { Counter = value };
    protected override int ReadValue(CounterEntity state) => state.Counter;
}

// ---------------------------------------------------------------------------
// Postgres multi-tenant adapter
// ---------------------------------------------------------------------------

/// <summary>
/// Runs <see cref="StateStoreSpecification{TState}"/> against
/// <see cref="PostgresStateStore{TState}"/> on the <em>multi-tenant</em> schema
/// — the one that includes the <c>tenant_id</c> column in
/// <c>alberto_projection_states</c>.
///
/// <para>
/// Every store here is constructed <em>with</em> a <c>tenantId</c>, because on this schema
/// there is no other option: <c>tenant_id</c> is <c>NOT NULL</c> and part of the primary key,
/// so a store built without one names an <c>ON CONFLICT</c> constraint that does not exist and
/// fails every write with <c>42P10</c>. That is why a cross-tenant projection on a tenant-enabled
/// module stores its single document under <c>TenantScope.CrossTenant</c> rather than under no
/// tenant at all — see <see cref="CrossTenantProjectionContractTests"/>.
/// </para>
/// <para>
/// <see cref="StateStoreSpecification{TState}.SupportsTenantIsolation"/> is
/// <c>true</c>: stores constructed <em>with</em> a <c>tenantId</c> via
/// <see cref="StateStoreSpecification{TState}.CreateStoreForTenant"/> must
/// isolate data between tenants.
/// </para>
/// </summary>
public sealed class MultiTenantPostgresStateStoreSpecificationTests(MultiTenantDbFixture fixture)
    : StateStoreSpecification<SimpleState>, IClassFixture<MultiTenantDbFixture>
{
    protected override bool SupportsTenantIsolation => true;

    /// <summary>
    /// Creates a store scoped to a per-test, fixed tenant. The multi-tenant schema
    /// enforces <c>tenant_id NOT NULL</c>, so a store without <c>tenantId</c> cannot
    /// write. Using a fixed per-test tenant lets the base-spec facts (upsert, delete,
    /// rebuild-version scoping, etc.) run against the multi-tenant schema without
    /// modification.
    /// </summary>
    protected override Task<IStateStore<SimpleState>> CreateStore(
        string projectionType,
        Func<int>? rebuildVersion = null) =>
        Task.FromResult<IStateStore<SimpleState>>(
            new PostgresStateStore<SimpleState>(
                fixture.DataSource,
                projectionType,
                rebuildVersion: rebuildVersion,
                tenantId: $"spec-tenant-{TestId}"));

    /// <summary>
    /// Creates a store scoped to an explicit <paramref name="tenantId"/> on the multi-tenant
    /// schema. Called by the <c>TenantId_*</c> isolation facts, which pass two different
    /// tenantId values to verify that documents are isolated between tenants.
    /// </summary>
    protected override Task<IStateStore<SimpleState>> CreateStoreForTenant(
        string projectionType,
        string tenantId,
        Func<int>? rebuildVersion = null) =>
        Task.FromResult<IStateStore<SimpleState>>(
            new PostgresStateStore<SimpleState>(
                fixture.DataSource,
                projectionType,
                rebuildVersion: rebuildVersion,
                tenantId: tenantId));

    protected override SimpleState MakeState(int value) => new() { Value = value };
    protected override int ReadValue(SimpleState state) => state.Value;
}
