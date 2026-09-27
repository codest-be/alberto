// Native AOT smoke test (#178): drives Alberto end to end against a real PostgreSQL from a
// natively compiled binary. Every step asserts; the first failure prints and exits 1.
//
//   ALBERTO_AOTSMOKE_CONNECTION="Host=...;Username=...;Password=...;Database=..." ./Alberto.AotSmoke
//
// Each run migrates a fresh schema, so it can be pointed at a shared database repeatedly.

using System.Diagnostics;
using Alberto;
using Alberto.AotSmoke;
using Alberto.Commands;
using Alberto.Messaging;
using Alberto.Messaging.Postgres;
using Alberto.Postgres;
using Alberto.Telemetry;
using Alberto.Upcasting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

const string ModuleKey = "smoke";
var timeout = TimeSpan.FromSeconds(60);

var connectionString = Environment.GetEnvironmentVariable("ALBERTO_AOTSMOKE_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("FAIL: set ALBERTO_AOTSMOKE_CONNECTION to a PostgreSQL connection string.");
    return 2;
}

var schema = $"aot_smoke_{Guid.NewGuid():N}"[..20];
var accountId = $"acc-{Guid.NewGuid():N}";
var accountBoundary = DcbQuery.For("account", accountId);

try
{
    Step("runtime is Native AOT", () =>
        Check(!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
            "dynamic code is supported — this binary was not published with PublishAot"),
        requireAot: true);

    // ---- migrate --------------------------------------------------------------------------
    Step("migrate", () =>
    {
        // EnsureDatabase = false is the least-privilege path (#182): the database exists, and the
        // migrator must not reach for the server's postgres maintenance database to check.
        var migrationLog = new CountingLogger();
        var result = PostgresMigrator.Migrate(connectionString, new MigrationOptions
        {
            Schema = schema,
            SingleTenant = true,
            Logger = migrationLog,
            EnsureDatabase = false,
        });
        Check(result.Successful, $"migration failed: {result.Error}");
        Check(result.ExecutedScripts.Count > 0, "no migration scripts ran on a fresh schema");
        Check(migrationLog.Count > 0, "DbUp's output did not reach the ILogger");
    });

    await using var dataSource = NpgsqlDataSource.Create(connectionString);
    var transport = new InMemoryTransport();
    var reacted = new TaskCompletionSource<AccountOpened>(TaskCreationOptions.RunContinuationsAsynchronously);

    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.AddAlberto(ModuleKey, module => module
        .WithPostgres(o => o with { ConnectionString = connectionString, Schema = schema, AutoMigrate = false })
        .WithEvents(SmokeEvents.Registry)
        .AddUpcaster(DeclareUpcaster.For<FundsDeposited>(FundsDeposited.Id)
            .From<FundsDepositedV1>(1, SmokeJsonContext.Default.FundsDepositedV1, v1 => new FundsDeposited(v1.AccountId, v1.Amount, "EUR"))
            .Build())
        .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(50) })
        .AddBatchConsumeMiddleware(_ => TelemetryBatchConsumeMiddleware.Create())
        .ReactTo<AccountOpened>(_ => (e, _) =>
        {
            reacted.TrySetResult(e);
            return Task.CompletedTask;
        }, "smoke-welcome")
        .WithOutbox(
            mappings => mappings.Map<AccountOpened, AccountOpenedMessage>(
                e => new AccountOpenedMessage(e.AccountId, e.Owner),
                SmokeJsonContext.Default.AccountOpenedMessage),
            new PostgresOutboxStore(dataSource, schema, multiTenant: false),
            transport));

    using var host = builder.Build();
    await host.StartAsync();

    var store = host.Services.CreateScope().ServiceProvider.GetRequiredKeyedService<AlbertoStore>(ModuleKey);
    var eventStore = host.Services.GetRequiredKeyedService<IEventStore>(ModuleKey);
    var evolver = new AccountEvolver();
    // The reflection fallback also runs under AOT (interpreted), so passing proves nothing unless
    // the generated table is what the dispatcher gets.
    Check(evolver is IGeneratedEvolver<AccountState>, "AccountEvolver has no generated dispatch table");

    // ---- append with tags -----------------------------------------------------------------
    await StepAsync("append with tags", async () =>
    {
        var result = await store.Handle(new OpenAccount(accountId, "Ada"))
            .Load(accountBoundary, evolver)
            .Decide((cmd, state) => state.Opened
                ? Decision.Fail("account.exists", "already open")
                : Decision.Succeed(new AccountOpened(cmd.AccountId, cmd.Owner)))
            .Commit(CancellationToken.None);
        Check(result.IsSuccess, $"open failed: {string.Join("; ", result.Problems.Select(p => p.Message))}");

        var stored = await eventStore.StreamAsync(accountBoundary);
        Check(stored.Count == 1, $"expected 1 event under the boundary, found {stored.Count}");
        var envelope = stored.Single();
        Check(envelope.EventType.Id == AccountOpened.Id, $"stored as '{envelope.EventType.Id}'");
        Check(envelope.Tags.Contains(new EventTag("account", accountId)), "the account tag was not written");
        Check(envelope.EventData.Contains("Ada"), $"payload lost the owner: {envelope.EventData}");
    });

    // ---- conditional append conflict ------------------------------------------------------
    await StepAsync("conditional append conflict", async () =>
    {
        // Position 0 says "nothing under this boundary yet", which the open above contradicts.
        var result = await store.Handle(new Deposit(accountId, 1m))
            .Decide(cmd => Decision.Succeed(new FundsDeposited(cmd.AccountId, cmd.Amount, "EUR")))
            .TryCommit(accountBoundary, expectedPosition: 0, CancellationToken.None);
        Check(result.IsFailure, "a stale expected position was accepted");
        Check(result.Problems.Any(p => p.Code == DcbConflictException.ProblemCode),
            $"expected {DcbConflictException.ProblemCode}, got {string.Join(", ", result.Problems.Select(p => p.Code))}");
    });

    // ---- upcast ---------------------------------------------------------------------------
    await StepAsync("upcast (v1 payload read as v2)", async () =>
    {
        // A deposit as an older build wrote it: schema v1, no currency, no _version tag.
        await eventStore.AppendAsync(
        [
            new EventToPersist
            {
                EventType = new EventType(FundsDeposited.Id, 1),
                Tags = [new EventTag("account", accountId)],
                EventData = $$"""{"AccountId":"{{accountId}}","Amount":10}""",
            },
        ]);

        var result = await store.Handle(new Deposit(accountId, 5m))
            .Load(accountBoundary, evolver)
            .Decide((cmd, state) => state.Currency == "EUR"
                ? Decision.Succeed(new FundsDeposited(cmd.AccountId, cmd.Amount, "EUR"))
                : Decision.Fail("upcast.missing", $"v1 deposit folded with currency '{state.Currency}'"))
            .Commit(CancellationToken.None);
        Check(result.IsSuccess, $"deposit failed: {string.Join("; ", result.Problems.Select(p => p.Message))}");

        var deposits = (await eventStore.StreamAsync(accountBoundary))
            .Where(e => e.EventType.Id == FundsDeposited.Id)
            .ToList();
        Check(deposits.Count == 2, $"expected 2 deposits, found {deposits.Count}");
        Check(deposits[^1].EventType.Version == 2, $"new deposit written at v{deposits[^1].EventType.Version}, not v2");
    });

    // ---- reconstitute via evolver ---------------------------------------------------------
    await StepAsync("reconstitute via evolver", async () =>
    {
        AccountState? seen = null;
        var result = await store.Handle(accountId)
            .Load(accountBoundary, evolver)
            .Decide(state =>
            {
                seen = state;
                return Decision.Succeed();
            })
            .Commit(CancellationToken.None);
        Check(result.IsSuccess, "load failed");
        Check(seen is { Opened: true, Owner: "Ada", Balance: 15m, Currency: "EUR" },
            $"reconstituted {seen}");
    });

    // ---- reactor --------------------------------------------------------------------------
    await StepAsync("reactor", async () =>
    {
        var e = await reacted.Task.WaitAsync(timeout);
        Check(e.AccountId == accountId, $"reactor saw '{e.AccountId}'");
    });

    // ---- outbox ---------------------------------------------------------------------------
    await StepAsync("outbox relay", async () =>
    {
        var clock = Stopwatch.StartNew();
        ExternalMessage? message = null;
        while (message is null && clock.Elapsed < timeout)
        {
            message = transport.Published.FirstOrDefault(m => m.MessageType == AccountOpenedMessage.Type);
            if (message is null)
                await Task.Delay(100);
        }

        Check(message is not null, "no message was relayed from the outbox");
        Check(message!.Payload.Contains(accountId), $"payload lost the account id: {message.Payload}");
        Check(message.Version == "1", $"message version '{message.Version}'");
    });

    await host.StopAsync();
}
catch (SmokeFailure failure)
{
    Console.Error.WriteLine($"FAIL: {failure.Message}");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL: unexpected {exception}");
    return 1;
}
finally
{
    await DropSchemaAsync(connectionString, schema);
}

Console.WriteLine("PASS: all Native AOT smoke steps succeeded.");
return 0;

static void Step(string name, Action body, bool requireAot = false)
{
    if (requireAot && Environment.GetEnvironmentVariable("ALBERTO_AOTSMOKE_ALLOW_JIT") == "1")
    {
        Console.WriteLine($"skip  {name} (ALBERTO_AOTSMOKE_ALLOW_JIT=1)");
        return;
    }

    body();
    Console.WriteLine($"ok    {name}");
}

static async Task StepAsync(string name, Func<Task> body)
{
    try
    {
        await body();
    }
    catch (SmokeFailure)
    {
        throw;
    }
    catch (Exception exception)
    {
        throw new SmokeFailure($"{name}: {exception}");
    }

    Console.WriteLine($"ok    {name}");
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new SmokeFailure(message);
}

static async Task DropSchemaAsync(string connectionString, string schema)
{
    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
        await cmd.ExecuteNonQueryAsync();
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"warn: could not drop schema {schema}: {exception.Message}");
    }
}

namespace Alberto.AotSmoke
{
    internal sealed class SmokeFailure(string message) : Exception(message);

    /// <summary>Counts what DbUp logs, to prove migration output goes through <see cref="ILogger"/>.</summary>
    internal sealed class CountingLogger : ILogger
    {
        public int Count { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Count++;
    }
}
