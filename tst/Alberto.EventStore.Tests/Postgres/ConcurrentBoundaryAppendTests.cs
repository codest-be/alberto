using Alberto.EventStore.Events;
using Alberto.EventStore.Exceptions;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Postgres;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Alberto.EventStore.Tests.Postgres;

/// <summary>
///     Proves the DCB concurrent-boundary race: two parallel appends that both assert
///     "no event of type case-opened for case X exists" must not both succeed.
///     Without a serialising mechanism only one winner is allowed; more than one means
///     the consistency boundary was breached.
/// </summary>
[Collection("Postgres Integration Tests")]
public class ConcurrentBoundaryAppendTests(PostgresTestFixture fixture)
{
    [Fact]
    public async Task Append_WithConsistencyBoundary_ConcurrentRace_AtMostOneSucceeds()
    {
        // Arrange – unique case id per test run so parallel test classes do not interfere
        var tenantId = fixture.GetNextTenantId();
        var tenant = new Tenant(tenantId.ToString());
        var caseId = Guid.NewGuid().ToString("N");

        var options = Options.Create(fixture.Options);
        var backend = new PostgresEventStoreBackend(
            options,
            new NullLoggerFactory().CreateLogger<PostgresEventStoreBackend>());

        // Consistency boundary: "no case-opened event for this case exists yet"
        var boundary = new StreamQuery()
            .WithTags(EventTag.Parse($"case:{caseId}"))
            .WithEventTypes(new EventType("case-opened"));

        const int concurrency = 25;

        // Act – launch all appends simultaneously; each independently claims
        //       "I am the first to open this case".
        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            var evt = new EventToPersist
            {
                EventType = new EventType("case-opened"),
                EventJson = """{"data":"test"}""",
                Tags = [EventTag.Parse($"case:{caseId}")],
                Metadata = [],
                Created = DateTimeOffset.UtcNow,
            };

            try
            {
                await backend.Append(tenant, [evt], boundary, null, TestContext.Current.CancellationToken);
                return true;   // this call won
            }
            catch (ConcurrencyConflictException)
            {
                return false;  // this call was correctly rejected
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);

        // Assert 1 – at most one caller may believe it succeeded
        Assert.True(
            successCount <= 1,
            $"Expected at most 1 successful append under concurrent load, but {successCount} appends succeeded. " +
            "The consistency boundary was breached: multiple callers all saw 'no conflict' and all inserted.");

        // Assert 2 – the database must hold at most one row for this boundary
        await using var conn = new NpgsqlConnection(fixture.Options.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var rowCount = await conn.QuerySingleAsync<int>(
            $"""
             SELECT COUNT(*)
             FROM {fixture.Options.Schema}.events
             WHERE tenant_id = @tid
               AND event_type = 'case-opened'
               AND tags @> ARRAY[@tag]::text[]
             """,
            new { tid = tenantId.ToString(), tag = $"case:{caseId}" });

        Assert.True(
            rowCount <= 1,
            $"Expected at most 1 row in the database, but found {rowCount}. " +
            "Duplicate events landed despite the consistency boundary — the race window is real.");

        await fixture.CleanupTestData(tenantId);
    }
}
