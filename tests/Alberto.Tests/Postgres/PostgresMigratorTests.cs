using Alberto.Postgres;
using Xunit;

namespace Alberto.Tests.Postgres;

[Trait("Category", "Integration")]
public sealed class PostgresMigratorTests(SingleTenantPostgresFixture fixture)
    : IClassFixture<SingleTenantPostgresFixture>
{
    [Fact]
    public void GetPendingMigrations_UsesTheSameSchemaLocalJournalAsMigrate()
    {
        var schema = $"migration_plan_{Guid.NewGuid():N}";

        var result = PostgresMigrator.Migrate(
            fixture.ConnectionString,
            schema,
            singleTenant: true);

        Assert.True(result.Successful, result.Error?.Message);
        Assert.Empty(PostgresMigrator.GetPendingMigrations(
                fixture.ConnectionString,
                schema,
                singleTenant: true));
    }
}
