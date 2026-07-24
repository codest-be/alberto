using System.Text.Json;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

[EventType("rebuild-e2e-scored")]
public sealed record Scored(string Player, int Points) : IEvent;

/// <summary>State of the projection under rebuild: one running total per player.</summary>
public sealed class Totals
{
    public int Points { get; set; }
}

public static class TotalsProjection
{
    public const string ProcessorId = "rebuild-e2e-totals";
    public const string ProjectionType = "rebuild-e2e-totals";

    public static readonly ProjectionDeclaration<Totals> Declaration =
        DeclareProjection.For<Totals>(ProcessorId)
            .On<Scored>(
                e => e.Player,
                (state, e, _) => new Totals { Points = state.Points + e.Points })
            .Build();
}

/// <summary>
/// A running application with rebuilds enabled, wired the way a consumer would wire it:
/// a Postgres-backed module with one projection whose store follows the rebuild version.
/// </summary>
public sealed class ProjectionRebuildHostFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public const string ModuleKey = "rebuild-e2e";

    public ServiceProvider Services { get; private set; } = null!;
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var connectionString = _container.GetConnectionString();

        var migration = PostgresMigrator.Migrate(connectionString, singleTenant: true);
        if (!migration.Successful)
            throw new InvalidOperationException($"Migration failed: {migration.Error?.Message}", migration.Error);

        DataSource = NpgsqlDataSource.Create(connectionString);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlberto(ModuleKey, builder => builder
            .WithPostgres(options =>
            {
                options.ConnectionString = connectionString;
                options.AutoMigrate = false;
                options.EnableNotifyListener = false;
            })
            .AddProjection(TotalsProjection.Declaration, ctx => () =>
                new PostgresStateStore<Totals>(
                    ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey),
                    TotalsProjection.ProjectionType,
                    rebuildVersion: ctx.RebuildVersion))
            .WithControlLoop(loop => loop
                .WithPollingInterval(TimeSpan.FromMilliseconds(50))
                .WithRebuilds(pollingInterval: TimeSpan.FromMilliseconds(100))));

        Services = services.BuildServiceProvider();

        foreach (var hosted in Services.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in Services.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        await Services.DisposeAsync();
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

/// <summary>
/// The rebuild mechanism end to end, against a real database and a real running control loop:
/// start a rebuild, let the coordinator replay the log into a shadow copy, and have it promoted
/// underneath a live projection nobody took offline.
/// </summary>
/// <remarks>
/// The tests share one host and one processor — a rebuild is per-processor state and two at
/// once would be testing the fixture rather than the mechanism — so each uses its own player
/// id and reads the rebuild version out of the state machine rather than assuming a number.
/// xUnit runs the tests in a class one at a time, which is what makes sharing the processor safe.
/// </remarks>
public sealed class ProjectionRebuildEndToEndTests(ProjectionRebuildHostFixture fixture)
    : IClassFixture<ProjectionRebuildHostFixture>
{
    private IEventStore EventStore =>
        fixture.Services.GetRequiredKeyedService<IEventStore>(ProjectionRebuildHostFixture.ModuleKey);

    private IProjectionRebuildStore RebuildStore =>
        fixture.Services.GetRequiredKeyedService<IProjectionRebuildStore>(ProjectionRebuildHostFixture.ModuleKey);

    /// <summary>
    /// The whole point of the feature: a projection with wrong data in it is corrected by a
    /// rebuild, and no reader ever sees it empty or half-built.
    /// </summary>
    [Fact]
    public async Task Rebuild_ReplacesCorruptedState_WithoutEverServingAPartialProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var player = NewPlayer();

        await AppendAsync(player, 3, ct);
        await AppendAsync(player, 4, ct);

        var versionBefore = await ActiveVersionAsync(ct);
        await WaitUntilAsync(
            async () => await ReadAsync(versionBefore, player, ct) is { Points: 7 },
            "the live projection to catch up", ct);

        // Corrupt what the projection serves, the way a bug in an Apply method would have.
        await WriteAsync(versionBefore, player, new Totals { Points = 999 }, ct);

        await StartRebuildAsync(ct);

        // While the rebuild runs, readers keep seeing the old version — wrong, but whole.
        // Any other value would mean the shadow loop is writing where readers can see it.
        var observed = new List<int?>();
        await WaitUntilAsync(
            async () =>
            {
                observed.Add((await ReadAsync(await ActiveVersionAsync(ct), player, ct))?.Points);
                return (await StateAsync(ct)).Status is RebuildStatus.Completed;
            },
            "the rebuild to be promoted", ct);

        observed.Should().OnlyContain(points => points == 999 || points == 7,
            "a reader moves from the complete old version straight to the complete new one");

        var promoted = await StateAsync(ct);
        promoted.ActiveVersion.Should().Be(versionBefore + 1);
        promoted.RebuildingVersion.Should().BeNull();

        var afterPromotion = await ReadAsync(promoted.ActiveVersion, player, ct);
        afterPromotion!.Points.Should().Be(7, "the rebuild replayed the log rather than copying the bad state");

        var superseded = await ReadAsync(versionBefore, player, ct);
        superseded.Should().BeNull("promotion drops the version it replaced in the same transaction");
    }

    /// <summary>
    /// Events that arrive while a rebuild is replaying must end up in the rebuilt version too.
    /// Otherwise promotion would quietly move the projection backwards in time.
    /// </summary>
    [Fact]
    public async Task Rebuild_CatchesUpOnEventsThatArriveWhileItIsRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var player = NewPlayer();

        await AppendAsync(player, 10, ct);

        var versionBefore = await ActiveVersionAsync(ct);
        await WaitUntilAsync(
            async () => await ReadAsync(versionBefore, player, ct) is { Points: 10 },
            "the live projection to catch up", ct);

        await StartRebuildAsync(ct);

        // Lands after the target position the rebuild was told to replay to.
        await AppendAsync(player, 5, ct);

        await WaitUntilAsync(
            async () =>
            {
                var state = await StateAsync(ct);
                return state.Status is RebuildStatus.Completed && state.ActiveVersion > versionBefore;
            },
            "the rebuild to be promoted", ct);

        var active = await ActiveVersionAsync(ct);
        await WaitUntilAsync(
            async () => await ReadAsync(active, player, ct) is { Points: 15 },
            "the promoted version to include the events appended mid-rebuild", ct);
    }

    /// <summary>
    /// An aborted rebuild leaves nothing behind: the version it was writing into is gone and
    /// the version readers see never moved.
    /// </summary>
    [Fact]
    public async Task AbortedRebuild_LeavesTheLiveVersionUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var player = NewPlayer();

        await AppendAsync(player, 8, ct);

        var versionBefore = await ActiveVersionAsync(ct);
        await WaitUntilAsync(
            async () => await ReadAsync(versionBefore, player, ct) is { Points: 8 },
            "the live projection to catch up", ct);

        var started = await StartRebuildAsync(ct);
        var abandonedVersion = started.RebuildingVersion!.Value;

        // Let the shadow loop actually write something, so the abort has state to discard
        // rather than trivially succeeding on an empty version.
        await WaitUntilAsync(
            async () => await ReadAsync(abandonedVersion, player, ct) is not null,
            "the shadow loop to write into the rebuilding version", ct);

        var outcome = await RebuildStore.AbortAsync(TotalsProjection.ProcessorId, ct);

        outcome.DiscardedVersion.Should().Be(abandonedVersion);
        outcome.State.ActiveVersion.Should().Be(versionBefore);
        outcome.State.RebuildingVersion.Should().BeNull();

        (await ReadAsync(versionBefore, player, ct))!.Points.Should().Be(8);
        (await ReadAsync(abandonedVersion, player, ct)).Should().BeNull();
    }

    /// <summary>
    /// A rebuild started after an aborted one must not be handed the aborted one's version
    /// number. Abort deletes that version's rows and flips the status in one transaction, but
    /// the shadow loop writing into it lives in the application process and only stops on a
    /// later poll — so anything it writes in between outlives the delete. Reusing the number
    /// seeds the fresh replay with those orphans and applies every event twice.
    /// </summary>
    [Fact]
    public async Task RebuildAfterAnAbort_GetsAFreshVersionNumber()
    {
        var ct = TestContext.Current.CancellationToken;
        var player = NewPlayer();

        await AppendAsync(player, 6, ct);

        var before = await ActiveVersionAsync(ct);
        await WaitUntilAsync(
            async () => await ReadAsync(before, player, ct) is { Points: 6 },
            "the live projection to catch up", ct);

        var aborted = await StartRebuildAsync(ct);
        var abandonedVersion = aborted.RebuildingVersion!.Value;

        await WaitUntilAsync(
            async () => await ReadAsync(abandonedVersion, player, ct) is not null,
            "the shadow loop to write into the rebuilding version", ct);

        await RebuildStore.AbortAsync(TotalsProjection.ProcessorId, ct);

        var second = await StartRebuildAsync(ct);

        second.ActiveVersion.Should().Be(before,
            "aborting must not move the version readers are served from");
        second.RebuildingVersion.Should().NotBe(abandonedVersion,
            "a version a shadow loop may still be writing into cannot be reallocated");

        await WaitUntilAsync(
            async () => await StateAsync(ct) is { Status: RebuildStatus.Completed } s
                        && s.ActiveVersion == second.RebuildingVersion,
            "the second rebuild to be promoted", ct);

        (await ReadAsync(second.RebuildingVersion!.Value, player, ct))!.Points.Should().Be(6,
            "replaying one event of 6 points onto an aborted rebuild's leftovers would read 12");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string NewPlayer() => $"p-{Guid.NewGuid():N}";

    private async Task AppendAsync(string player, int points, CancellationToken ct) =>
        await EventStore.AppendAsync(
            [
                new EventToPersist
                {
                    EventType = new EventType(EventTypeAttribute.GetEventTypeId(typeof(Scored))),
                    Tags = [new EventTag("player", player)],
                    EventData = JsonSerializer.Serialize(new Scored(player, points)),
                }
            ],
            cancellationToken: ct);

    /// <summary>
    /// Starts a rebuild the way the CLI does: capture the head as the target, then hand the
    /// state machine the transition and let the coordinator notice.
    /// </summary>
    private async Task<ProjectionRebuildState> StartRebuildAsync(CancellationToken ct)
    {
        var head = await new PostgresAdminDataAccess(fixture.DataSource).GetGlobalPositionAsync(ct) ?? 0;
        return await RebuildStore.StartAsync(
            TotalsProjection.ProcessorId, TotalsProjection.ProjectionType, head, ct);
    }

    private Task<ProjectionRebuildState> StateAsync(CancellationToken ct) =>
        RebuildStore.GetAsync(TotalsProjection.ProcessorId, TotalsProjection.ProjectionType, ct);

    private async Task<int> ActiveVersionAsync(CancellationToken ct) => (await StateAsync(ct)).ActiveVersion;

    /// <summary>
    /// A state store pinned to one version, for looking at what a rebuild did to storage rather
    /// than at what the application happens to be serving.
    /// </summary>
    private PostgresStateStore<Totals> StoreAtVersion(int version) =>
        new(fixture.DataSource, TotalsProjection.ProjectionType, rebuildVersion: () => version);

    private async Task<Totals?> ReadAsync(int version, string player, CancellationToken ct)
    {
        var states = await StoreAtVersion(version).LoadManyAsync([player], ct: ct);
        return states.GetValueOrDefault(player);
    }

    private Task WriteAsync(int version, string player, Totals state, CancellationToken ct) =>
        StoreAtVersion(version).ApplyChangesAsync(
            new Dictionary<string, Totals> { [player] = state }, [], ct: ct);

    /// <summary>
    /// Polls until a condition holds, failing with what it was waiting for rather than with a
    /// bare timeout. The coordinator is a background loop; there is nothing to await on it.
    /// </summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(50, ct);
        }

        throw new TimeoutException($"Timed out after 30s waiting for {what}.");
    }
}
