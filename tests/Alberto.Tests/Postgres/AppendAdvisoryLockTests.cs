using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Guards the width of the append advisory-lock key.
/// </summary>
/// <remarks>
/// <para>
/// The append lock serializes the DCB conflict check, so two appends that could overlap must
/// agree on a lock — and, just as importantly, two that could <em>not</em> overlap must not.
/// Multi-tenant append throughput is entirely a consequence of the second half: the key is
/// <c>alberto-append:{schema}:{tenantId}</c> precisely so different tenants proceed in parallel.
/// </para>
/// <para>
/// The failure this guards against is silent. Under the earlier
/// <c>pg_advisory_xact_lock(1, hashtext(key))</c> the discriminating space was
/// <c>hashtext</c>'s 32 bits, and two colliding tenants simply serialized against each other —
/// no error, no log line, nothing in application telemetry, just a throughput cliff that gets
/// worse as tenant count grows. Nothing in the append path can observe the difference, which is
/// why the property is asserted against the database rather than inferred from behaviour.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AppendAdvisoryLockTests(PostgresCluster cluster)
{
    /// <summary>
    /// How many candidate tenant keys to search for a <c>hashtext</c> collision.
    /// </summary>
    /// <remarks>
    /// Expected colliding pairs ≈ n²/2³³, so 500k gives ≈29 — the probability of finding none
    /// is e⁻²⁹, which is what keeps this from being a flaky test. 200k would give ≈4.6 and fail
    /// to find a pair roughly 1% of the time.
    /// </remarks>
    private const int CandidateKeys = 500_000;

    /// <summary>
    /// Finds one pair of distinct keys sharing a <c>hashtext</c> value.
    /// </summary>
    /// <remarks>
    /// Grouping rather than self-joining is not a stylistic choice: the obvious
    /// <c>JOIN keys b ON hashtext(a.k) = hashtext(b.k)</c> plans as a hash join that has to
    /// materialize both sides of a half-million-row CTE and does not finish inside the default
    /// command timeout. A hash aggregate makes it a single pass. Within a group of
    /// <c>count(*) &gt; 1</c> the keys are distinct strings, so <c>min</c> and <c>max</c> are a
    /// valid pair.
    /// </remarks>
    // Not const: C# only folds interpolations of string constants, and CandidateKeys is an int.
    private static readonly string FindCollisionSql =
        $"""
        SELECT min(k), max(k)
        FROM (
            SELECT 'alberto-append:public:tenant-' || g AS k
            FROM generate_series(1, {CandidateKeys}) g
        ) s
        GROUP BY hashtext(k)
        HAVING count(*) > 1
        LIMIT 1
        """;

    /// <summary>
    /// Two tenant keys that <c>hashtext</c> maps together must land on different
    /// <c>hashtextextended</c> values.
    /// </summary>
    /// <remarks>
    /// The colliding pair is found by asking the server rather than hard-coded, so the test
    /// does not pin itself to one PostgreSQL version's hash function.
    /// </remarks>
    [Fact]
    public async Task Colliding_hashtext_keys_do_not_collide_in_the_64_bit_space()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await OpenAsync(await FreshDatabaseAsync(ct), ct);
        var (key1, key2) = await FindHashtextCollisionAsync(conn, ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT hashtextextended(@k1, 0), hashtextextended(@k2, 0)", conn);
        cmd.Parameters.AddWithValue("k1", key1);
        cmd.Parameters.AddWithValue("k2", key2);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        reader.GetInt64(0).Should().NotBe(reader.GetInt64(1),
            because: $"'{key1}' and '{key2}' share a hashtext value, so under the old " +
                     "pg_advisory_xact_lock(1, hashtext(key)) form these two tenants' appends " +
                     "serialized against each other; hashtextextended must separate them");
    }

    /// <summary>
    /// The lock the append path actually takes is the single-argument 64-bit form, and two
    /// tenants colliding under <c>hashtext</c> can hold it simultaneously.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This demonstrates what the previous test only implies: it acquires both locks on two live
    /// connections at once. Under the 32-bit form the second acquisition would block until the
    /// first transaction ended, so the test would hang rather than fail — hence the explicit
    /// <c>lock_timeout</c>, which turns that hang into a diagnosable error.
    /// </para>
    /// <para>
    /// Both connections must target the <em>same</em> database. PostgreSQL puts the database OID
    /// in the advisory lock tag, so connections to different databases never contend and the
    /// test would pass without proving anything.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_hashtext_colliding_tenants_can_hold_the_append_lock_concurrently()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await FreshDatabaseAsync(ct);

        await using var first = await OpenAsync(connectionString, ct);
        await using var second = await OpenAsync(connectionString, ct);

        var (key1, key2) = await FindHashtextCollisionAsync(first, ct);

        await using var tx1 = await first.BeginTransactionAsync(ct);
        await using var tx2 = await second.BeginTransactionAsync(ct);

        // Without this, a regression to the 32-bit key blocks the test run indefinitely
        // instead of reporting a failure.
        await ExecuteAsync(second, tx2, "SET LOCAL lock_timeout = '5s'", ct);

        await AcquireAppendLockAsync(first, tx1, key1, ct);

        var acquireSecond = async () => await AcquireAppendLockAsync(second, tx2, key2, ct);

        await acquireSecond.Should().NotThrowAsync(
            because: $"'{key1}' and '{key2}' are different tenants — they collide only under " +
                     "hashtext's 32 bits, and the append lock must not serialize them");
    }

    /// <summary>
    /// The same lock must still serialize two appends that share a key, or it is not doing the
    /// job the DCB conflict check depends on.
    /// </summary>
    /// <remarks>
    /// Widening the key space is only safe if it did not widen it so far that identical keys stop
    /// meeting. Asserting the negative case alone would be satisfied by a lock that never
    /// contends at all.
    /// </remarks>
    [Fact]
    public async Task The_same_tenant_key_still_serializes()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await FreshDatabaseAsync(ct);

        const string sharedKey = "alberto-append:public:tenant-acme";

        await using var first = await OpenAsync(connectionString, ct);
        await using var second = await OpenAsync(connectionString, ct);

        await using var tx1 = await first.BeginTransactionAsync(ct);
        await using var tx2 = await second.BeginTransactionAsync(ct);

        // Short, because the expected outcome is the timeout firing.
        await ExecuteAsync(second, tx2, "SET LOCAL lock_timeout = '2s'", ct);

        await AcquireAppendLockAsync(first, tx1, sharedKey, ct);

        var acquireSecond = async () => await AcquireAppendLockAsync(second, tx2, sharedKey, ct);

        (await acquireSecond.Should().ThrowAsync<PostgresException>(
                because: "two appends under the same key must not evaluate the DCB conflict " +
                         "check concurrently"))
            .Which.SqlState.Should().Be(PostgresErrorCodes.LockNotAvailable);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls the production lock acquisition rather than restating its SQL, so a regression in
    /// <c>PostgresBackendHelpers</c> fails these tests instead of leaving them green against a
    /// copy. Reached through <c>InternalsVisibleTo</c>.
    /// </summary>
    private static Task AcquireAppendLockAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string lockKey, CancellationToken ct) =>
        PostgresBackendHelpers.AcquireAppendLockAsync(conn, tx, lockKey, ct);

    private static async Task<(string Key1, string Key2)> FindHashtextCollisionAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(FindCollisionSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // No collision among 500k keys in a 32-bit space would mean the search is broken, not
        // that the risk is absent — so this is a failure rather than a skip.
        (await reader.ReadAsync(ct)).Should().BeTrue(
            because: $"{CandidateKeys:N0} keys in hashtext's 32-bit space collide with " +
                     "overwhelming probability");

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private Task<string> FreshDatabaseAsync(CancellationToken ct) =>
        cluster.CloneAsync(PostgresTemplates.Empty, nameof(AppendAdvisoryLockTests), ct);

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
