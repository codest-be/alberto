using System.Collections.Concurrent;
using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Migrations route DbUp's output through <see cref="ILogger"/>, and can run as a role that may
/// connect only to its own database (#182).
/// </summary>
/// <remarks>
/// The least-privilege role here owns its database and nothing else, and <c>CONNECT</c> on the
/// server's <c>postgres</c> database is revoked from <c>PUBLIC</c>, so it is refused there the way a
/// managed server's app role typically is. The suite otherwise connects as the superuser, which
/// the revoke does not affect.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MigrationLeastPrivilegeTests(PostgresCluster cluster)
{
    [Fact]
    public async Task Migrate_reports_through_the_logger_it_is_given()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await cluster.CloneAsync(PostgresTemplates.Empty, "migratelogger", ct);
        var logger = new CapturingLogger();

        var result = PostgresMigrator.Migrate(
            database, new MigrationOptions { Schema = "logged", SingleTenant = true, Logger = logger });

        result.Successful.Should().BeTrue(because: result.Error?.Message);
        logger.Messages.Should().Contain(m => m.Contains("Executing Database Server script"),
            because: "DbUp's progress must reach the host's logging, not the console");
    }

    [Fact]
    public async Task The_catalog_migration_reports_through_the_logger_it_is_given()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await cluster.CloneAsync(PostgresTemplates.Empty, "catalogmigratelogger", ct);
        var logger = new CapturingLogger();

        var result = PostgresCatalogMigrator.Migrate(
            database, new MigrationOptions { Schema = "catalog_logged", Logger = logger });

        result.Successful.Should().BeTrue(because: result.Error?.Message);
        logger.Messages.Should().Contain(m => m.Contains("Executing Database Server script"));
    }

    [Fact]
    public async Task A_role_that_cannot_reach_the_postgres_database_needs_EnsureDatabase_off()
    {
        var ct = TestContext.Current.CancellationToken;
        var restricted = await RestrictedDatabaseAsync(ct);

        var withEnsure = () => PostgresMigrator.Migrate(
            restricted, new MigrationOptions { Schema = "least_privilege", SingleTenant = true });

        // Proves the setup: without the option the role is refused at the maintenance database.
        withEnsure.Should().Throw<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        var result = PostgresMigrator.Migrate(
            restricted,
            new MigrationOptions { Schema = "least_privilege", SingleTenant = true, EnsureDatabase = false });

        result.Successful.Should().BeTrue(because: result.Error?.Message);
        result.ExecutedScripts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_host_migrates_at_startup_as_a_least_privilege_role_and_logs_through_ILogger()
    {
        var ct = TestContext.Current.CancellationToken;
        var restricted = await RestrictedDatabaseAsync(ct);
        var logs = new CapturingLoggerProvider();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddAlberto("least_privilege", module => module
            .WithPostgres(o => o with
            {
                ConnectionString = restricted,
                Schema = "least_privilege",
                EnsureDatabase = false,
                EnableNotifyListener = false,
                MaxPoolSize = 5,
            }));

        using var host = builder.Build();
        await host.StartAsync(ct);
        await host.StopAsync(ct);

        logs.Messages.Should().Contain(m => m.Contains("Executing Database Server script"));
    }

    /// <summary>
    /// Creates a login role and a database it owns, and returns a connection string for that role
    /// on that database. The role cannot connect to the server's <c>postgres</c> database.
    /// </summary>
    private async Task<string> RestrictedDatabaseAsync(CancellationToken ct)
    {
        var admin = await cluster.CloneAsync(PostgresTemplates.Empty, "leastprivilegeadmin", ct);
        var name = $"alb_lp_{Guid.NewGuid():N}"[..20];
        const string password = "least-privilege";

        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(ct);
            foreach (var sql in new[]
            {
                $"""CREATE ROLE "{name}" LOGIN PASSWORD '{password}'""",
                $"""CREATE DATABASE "{name}" OWNER "{name}" """,
                "REVOKE CONNECT ON DATABASE postgres FROM PUBLIC",
            })
            {
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        return new NpgsqlConnectionStringBuilder(admin)
        {
            Database = name,
            Username = name,
            Password = password,
            Pooling = false,
        }.ConnectionString;
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _messages.Enqueue(formatter(state, exception));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly CapturingLogger _logger = new();

        public IReadOnlyCollection<string> Messages => _logger.Messages;

        public ILogger CreateLogger(string categoryName) => _logger;

        public void Dispose() { }
    }
}
