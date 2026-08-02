using Alberto.Postgres;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Regression guard for the multi-tenant concurrent-append correctness bug introduced
/// by 003_AppendSetBased.sql: re-reading <c>alberto_events</c> over a positional range
/// silently drops events when two tenants' appends interleave on the shared
/// <c>global_position BIGSERIAL</c> sequence.
///
/// The multi-tenant advisory lock is keyed <c>alberto-append:{schema}:{tenantId}</c>,
/// so concurrent appends from different tenants proceed in parallel. That means tenant A
/// appending 3 events may receive positions {1, 3, 5} while tenant B takes {2, 4}.
/// The buggy range query <c>global_position > v_last_position - batch_size</c> for A
/// yields the range (2, 5] = {3, 4, 5}; filtered to <c>tenant_id = A</c> that returns
/// only {3, 5} — silently dropping position 1.
/// </summary>
public sealed class AppendConcurrentMultiTenantTests(MultiTenantDbFixture fixture)
    : IClassFixture<MultiTenantDbFixture>
{
    /// <summary>
    /// Fires concurrent appends from two tenants in tight loops and asserts that each
    /// tenant's returned envelopes exactly match the events it submitted — correct count,
    /// correct IDs, correct tenant stamp.
    ///
    /// Against the buggy positional-range RETURN QUERY this test fails intermittently
    /// but reliably: with 20 rounds of 3-event concurrent appends the BIGSERIAL sequence
    /// interleaves often enough to expose the drop.  After the fix (returning rows
    /// directly from the INSERT RETURNING output) it passes consistently.
    /// </summary>
    [Fact]
    public async Task ConcurrentTenantAppends_EachReturnExactlyOwnEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        const string TenantA = "conc-tenant-a";
        const string TenantB = "conc-tenant-b";
        const int Rounds = 20;
        const int EventsPerRound = 3;

        var backend = new PostgresTenantEventStoreBackend(fixture.DataSource);

        var failures = new List<string>();

        for (int round = 0; round < Rounds; round++)
        {
            // Build events for each tenant with explicit IDs so we can verify
            // the returned set exactly.
            var aIds = Enumerable.Range(0, EventsPerRound).Select(_ => Guid.NewGuid()).ToArray();
            var bIds = Enumerable.Range(0, EventsPerRound).Select(_ => Guid.NewGuid()).ToArray();

            var aEvents = aIds.Select(id => (IEventToPersist)new EventToPersist
            {
                Id = id,
                EventType = new EventType("conc-test"),
                Tags = [new EventTag("batch", $"a-{round}")],
                EventData = "{}",
            }).ToArray();

            var bEvents = bIds.Select(id => (IEventToPersist)new EventToPersist
            {
                Id = id,
                EventType = new EventType("conc-test"),
                Tags = [new EventTag("batch", $"b-{round}")],
                EventData = "{}",
            }).ToArray();

            // Fire both appends concurrently so the shared sequence interleaves.
            var aTask = backend.AppendForTenant(TenantA, aEvents, cancellationToken: ct);
            var bTask = backend.AppendForTenant(TenantB, bEvents, cancellationToken: ct);
            var aResults = await aTask;
            var bResults = await bTask;

            // ── Tenant A assertions ───────────────────────────────────────────

            if (aResults.Count != EventsPerRound)
                failures.Add($"Round {round}: TenantA returned {aResults.Count} envelopes, expected {EventsPerRound}");

            var aReturnedIds = aResults.Select(e => e.Id).ToHashSet();
            var aExpectedIds = aIds.ToHashSet();
            if (!aReturnedIds.SetEquals(aExpectedIds))
                failures.Add($"Round {round}: TenantA returned IDs {string.Join(",", aReturnedIds)} != submitted {string.Join(",", aExpectedIds)}");

            var aWrongTenant = aResults.Where(e => e.TenantId != TenantA).ToList();
            if (aWrongTenant.Count > 0)
                failures.Add($"Round {round}: TenantA received {aWrongTenant.Count} events with wrong tenant");

            // ── Tenant B assertions ───────────────────────────────────────────

            if (bResults.Count != EventsPerRound)
                failures.Add($"Round {round}: TenantB returned {bResults.Count} envelopes, expected {EventsPerRound}");

            var bReturnedIds = bResults.Select(e => e.Id).ToHashSet();
            var bExpectedIds = bIds.ToHashSet();
            if (!bReturnedIds.SetEquals(bExpectedIds))
                failures.Add($"Round {round}: TenantB returned IDs {string.Join(",", bReturnedIds)} != submitted {string.Join(",", bExpectedIds)}");

            var bWrongTenant = bResults.Where(e => e.TenantId != TenantB).ToList();
            if (bWrongTenant.Count > 0)
                failures.Add($"Round {round}: TenantB received {bWrongTenant.Count} events with wrong tenant");
        }

        failures.Should().BeEmpty(
            because: "every tenant's append must return exactly its own submitted events, " +
                     "regardless of concurrent appends from other tenants on the shared sequence");
    }
}
