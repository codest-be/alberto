using Alberto.Dcb.Tests.Infrastructure;

namespace Alberto.Dcb.Tests;

/// <summary>
/// A private database on the shared cluster, migrated multi-tenant
/// (includes the <c>tenant_id</c> column on <c>alberto_projection_states</c>).
/// Used by <see cref="Subscriptions.MultiTenantPostgresStateStoreSpecificationTests"/>
/// and any other test classes that need the multi-tenant schema without the full
/// DI wiring that <see cref="Tenancy.MultiTenantPostgresFixture"/> sets up.
/// </summary>
public sealed class MultiTenantDbFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.MultiTenant);
