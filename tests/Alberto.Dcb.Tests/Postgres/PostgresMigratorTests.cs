using Alberto.Dcb.Postgres;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Postgres;

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

        result.Successful.Should().BeTrue(because: result.Error?.Message);
        PostgresMigrator.GetPendingMigrations(
                fixture.ConnectionString,
                schema,
                singleTenant: true)
            .Should().BeEmpty();
    }
}
