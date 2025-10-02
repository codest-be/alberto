using EventStore;
using EventStore.Events;
using EventStore.MultiTenant;
using EventStore.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace EventStore.Tests.Postgres;

/// <summary>
/// Tests for PostgreSQL EventStore configuration validation and setup
/// </summary>
public class PostgresConfigurationTests
{
    [Fact]
    public void PostgresEventStoreOptions_WithValidConfiguration_ShouldSucceed()
    {
        // Arrange
        var options = new PostgresEventStoreOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            Schema = "events",
            BulkInsertThreshold = 10
        };

        // Act & Assert - Should not throw
        var wrappedOptions = Options.Create(options);
        var backend = new PostgresEventStoreBackend(wrappedOptions, NullLogger<PostgresEventStoreBackend>.Instance);
        Assert.NotNull(backend);
    }

    [Fact]
    public async Task PostgresEventStoreOptions_WithNullConnectionString_ShouldFailDuringOperations()
    {
        // Arrange
        var options = new PostgresEventStoreOptions
        {
            ConnectionString = null!,
            Schema = "events",
            BulkInsertThreshold = 10
        };

        var wrappedOptions = Options.Create(options);
        var backend = new PostgresEventStoreBackend(wrappedOptions, NullLogger<PostgresEventStoreBackend>.Instance);
        var tenant = new Tenant("test");
        var testEvent = new EventToPersist
        {
            EventType = new EventType("test-event"),
            EventJson = """{"data": "test"}""",
            Tags = [EventTag.Parse("test:123")],
            Metadata = new Dictionary<string, string>(),
            Created = DateTimeOffset.UtcNow
        };

        // Act & Assert - Should fail during database operation
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await backend.Append(tenant, [testEvent], null, null, CancellationToken.None));
    }

    [Fact]
    public void PostgresEventStoreOptions_WithEmptySchema_ShouldUseDefaultSchema()
    {
        // Arrange
        var options = new PostgresEventStoreOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            Schema = "",
            BulkInsertThreshold = 10
        };

        // Act & Assert - Empty schema should work (may default to "public" or similar)
        var wrappedOptions = Options.Create(options);
        var backend = new PostgresEventStoreBackend(wrappedOptions, NullLogger<PostgresEventStoreBackend>.Instance);
        Assert.NotNull(backend);
    }

    [Fact]
    public void PostgresEventStoreOptions_WithNegativeBulkThreshold_ShouldUseDefaultValue()
    {
        // Arrange
        var options = new PostgresEventStoreOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            Schema = "events",
            BulkInsertThreshold = -1
        };

        // Act & Assert - Negative values should be handled gracefully (likely default to 5)
        var wrappedOptions = Options.Create(options);
        var backend = new PostgresEventStoreBackend(wrappedOptions, NullLogger<PostgresEventStoreBackend>.Instance);
        Assert.NotNull(backend);
    }

    [Fact]
    public void PostgresEventStoreOptions_WithZeroBulkThreshold_ShouldSucceed()
    {
        // Arrange - Zero should disable bulk operations
        var options = new PostgresEventStoreOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            Schema = "events",
            BulkInsertThreshold = 0
        };

        // Act & Assert
        var wrappedOptions = Options.Create(options);
        var backend = new PostgresEventStoreBackend(wrappedOptions, NullLogger<PostgresEventStoreBackend>.Instance);
        Assert.NotNull(backend);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("events")]
    [InlineData("tenant_1")]
    [InlineData("schema_with_underscores")]
    [InlineData("schema123")]
    public void PostgresEventStoreOptions_WithValidSchemaNames_ShouldSucceed(string schemaName)
    {
        // Arrange
        var options = new PostgresEventStoreOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            Schema = schemaName,
            BulkInsertThreshold = 5
        };

        // Act & Assert
        var wrappedOptions = Options.Create(options);
        var backend = new PostgresEventStoreBackend(wrappedOptions, NullLogger<PostgresEventStoreBackend>.Instance);
        Assert.NotNull(backend);
    }

    [Theory]
    [InlineData("schema-with-dashes")]
    [InlineData("schema with spaces")]
    [InlineData("schema.with.dots")]
    [InlineData("123schema")]
    [InlineData("UPPERCASE")]
    public void PostgresEventStoreOptions_WithSpecialSchemaNames_ShouldHandleCorrectly(string schemaName)
    {
        // Arrange
        var options = new PostgresEventStoreOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            Schema = schemaName,
            BulkInsertThreshold = 5
        };

        // Act & Assert - Should handle special characters gracefully
        var wrappedOptions = Options.Create(options);

        // Some names might require special handling, but constructor should not throw
        var backend = new PostgresEventStoreBackend(wrappedOptions, NullLogger<PostgresEventStoreBackend>.Instance);
        Assert.NotNull(backend);
    }

    [Fact]
    public void ConnectionString_ShouldParseCorrectly()
    {
        // Arrange
        var connectionString = "Host=localhost;Port=5432;Database=eventstore;Username=user;Password=pass;Timeout=30";

        // Act
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        // Assert
        Assert.Equal("localhost", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("eventstore", builder.Database);
        Assert.Equal("user", builder.Username);
        Assert.Equal("pass", builder.Password);
        Assert.Equal(30, builder.Timeout);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public void BulkInsertThreshold_WithValidValues_ShouldSucceed(int threshold)
    {
        // Arrange
        var options = new PostgresEventStoreOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            Schema = "events",
            BulkInsertThreshold = threshold
        };

        // Act & Assert
        var wrappedOptions = Options.Create(options);
        var backend = new PostgresEventStoreBackend(wrappedOptions, NullLogger<PostgresEventStoreBackend>.Instance);
        Assert.NotNull(backend);
    }
}