using Alberto.Tests.Infrastructure;

namespace Alberto.Tests;

/// <summary>
/// A private database on the shared cluster, migrated single-tenant
/// (no tenant_id columns in the events tables).
/// </summary>
public sealed class SingleTenantPostgresFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.SingleTenant);
