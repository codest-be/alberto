using Alberto.Dcb.Tests.Infrastructure;

namespace Alberto.Dcb.Tests;

/// <summary>
/// A private database on the shared cluster, migrated multi-tenant
/// (tenant_id columns present).
/// </summary>
public sealed class PostgresFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.MultiTenant);
