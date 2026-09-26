using Alberto.Postgres;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

        result.Successful.Should().BeTrue(because: result.Error?.Message);
        PostgresMigrator.GetPendingMigrations(
                fixture.ConnectionString,
                schema,
                singleTenant: true)
            .Should().BeEmpty();
    }

    [Fact]
    public void Migrate_WithLoggerAndWithoutEnsureDatabase_LogsThroughItAndSucceeds()
    {
        // A least-privilege role cannot connect to the postgres maintenance database, so
        // ensureDatabase: false must skip that connection entirely; the target database
        // already exists here, which is the deployment shape the option is for (#182).
        var schema = $"migration_log_{Guid.NewGuid():N}";
        var logs = new List<string>();
        var logger = new ListLogger(logs);

        var result = PostgresMigrator.Migrate(
            fixture.ConnectionString,
            schema,
            singleTenant: true,
            logger: logger,
            ensureDatabase: false);

        result.Successful.Should().BeTrue(because: result.Error?.Message);
        logs.Should().NotBeEmpty("DbUp output must flow through the supplied ILogger");
    }

    private sealed class ListLogger(List<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (logs) logs.Add(formatter(state, exception));
        }
    }
}
