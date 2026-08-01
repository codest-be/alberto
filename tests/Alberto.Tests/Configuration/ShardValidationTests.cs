using System.Collections.Immutable;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Postgres;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Covers the sharding validation codes ALB0010–ALB0016. Every one of them is a mistake that
/// would otherwise surface as data in the wrong database.
/// </summary>
public class ShardValidationTests
{
    /// <summary>
    /// Minimal backend descriptor that explicitly does NOT support tenancy.
    /// Used to test ALB0011 (sharding requires a tenancy-capable backend).
    /// <see cref="InMemoryBackendDescriptor"/> now declares SupportsTenancy=true following
    /// the fix:inmemory-fidelity batch change, so this fake fills the role it previously played.
    /// </summary>
    private sealed class NoTenancyBackend : IAlbertoBackendDescriptor
    {
        public string Name => "NoTenancy";
        public bool SupportsTenancy => false;
        public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration section) => this;
        public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) => [];
        public void Register(AlbertoModuleContext context) { }
    }
    private const string Db1 = "Host=db1;Database=alberto;Username=x;Password=y";
    private const string Db2 = "Host=db2;Database=alberto;Username=x;Password=y";
    private const string Catalog = "Host=control;Database=alberto_catalog;Username=x;Password=y";

    private static PostgresBackendDescriptor Postgres(string connectionString, string? schema = null) =>
        new(new PostgresOptions { ConnectionString = connectionString, Schema = schema ?? "public" });

    private static AlbertoModuleDefinition Sharded(
        TenancyDefinition? tenancy = null,
        bool tenancyEnabled = true,
        IAlbertoBackendDescriptor? backend = null) => new()
    {
        ModuleKey = "orders",
        Backend = backend ?? Postgres(Db1),
        TenancyEnabled = tenancyEnabled,
        Tenancy = tenancy ?? new TenancyDefinition
        {
            Shards =
            [
                new ShardDeclaration("db1", Postgres(Db1)),
                new ShardDeclaration("db2", Postgres(Db2)),
            ],
            Catalog = Postgres(Catalog),
        },
    };

    private static IReadOnlyList<string> Codes(AlbertoModuleDefinition definition) =>
        new AlbertoModuleValidator().Collect(definition).Select(f => f.Code).ToArray();

    [Fact]
    public void A_well_formed_sharded_module_produces_no_failures()
    {
        Codes(Sharded()).Should().BeEmpty();
    }

    [Fact]
    public void An_unsharded_module_runs_none_of_the_shard_checks()
    {
        // The default path must stay exactly as it was — no catalog, no shards, no complaints.
        var definition = Sharded(new TenancyDefinition());

        Codes(definition).Should().BeEmpty();
    }

    [Fact]
    public void Shards_without_tenancy_fail_with_ALB0010()
    {
        Codes(Sharded(tenancyEnabled: false)).Should().Contain("ALB0010");
    }

    [Fact]
    public void Shards_on_a_backend_that_cannot_do_tenancy_fail_with_ALB0011()
    {
        // InMemoryBackendDescriptor now declares SupportsTenancy=true (fix:inmemory-fidelity),
        // so it no longer triggers this validation path. NoTenancyBackend explicitly returns
        // false to keep the validator path exercised.
        Codes(Sharded(backend: new NoTenancyBackend())).Should().Contain("ALB0011");
    }

    [Fact]
    public void An_unsafe_shard_id_fails_with_ALB0012()
    {
        // A shard id becomes a DI key, a metric tag and a lease holder name.
        var definition = Sharded(new TenancyDefinition
        {
            Shards = [new ShardDeclaration("EU-West", Postgres(Db1))],
            Catalog = Postgres(Catalog),
        });

        Codes(definition).Should().Contain("ALB0012");
    }

    [Fact]
    public void Duplicate_shard_ids_fail_with_ALB0012()
    {
        var definition = Sharded(new TenancyDefinition
        {
            Shards =
            [
                new ShardDeclaration("db1", Postgres(Db1)),
                new ShardDeclaration("db1", Postgres(Db2)),
            ],
            Catalog = Postgres(Catalog),
        });

        Codes(definition).Should().Contain("ALB0012");
    }

    [Fact]
    public void A_default_shard_that_is_not_declared_fails_with_ALB0013()
    {
        var definition = Sharded(new TenancyDefinition
        {
            Shards = [new ShardDeclaration("db1", Postgres(Db1))],
            Catalog = Postgres(Catalog),
            DefaultShardId = "db9",
        });

        Codes(definition).Should().Contain("ALB0013");
    }

    [Fact]
    public void A_declared_default_shard_is_accepted()
    {
        var definition = Sharded(new TenancyDefinition
        {
            Shards = [new ShardDeclaration("db1", Postgres(Db1))],
            Catalog = Postgres(Catalog),
            DefaultShardId = "db1",
        });

        Codes(definition).Should().BeEmpty();
    }

    [Fact]
    public void Shards_without_a_catalog_fail_with_ALB0014()
    {
        var definition = Sharded(new TenancyDefinition
        {
            Shards = [new ShardDeclaration("db1", Postgres(Db1))],
        });

        Codes(definition).Should().Contain("ALB0014");
    }

    [Fact]
    public void A_shard_that_exists_only_in_configuration_fails_with_ALB0015()
    {
        // Services are registered before configuration is read, so this shard would have no data
        // source, no migration and no control loops.
        var definition = Sharded() with
        {
            Tenancy = Sharded().Tenancy with { UndeclaredConfiguredShards = ["db3"] },
        };

        var failure = new AlbertoModuleValidator().Collect(definition)
            .Should().ContainSingle(f => f.Code == "ALB0015").Subject;

        failure.Problem.Should().Contain("db3");
        failure.Remedy.Should().Contain("AddShard(\"db3\"");
    }

    [Fact]
    public void Two_shards_pointing_at_the_same_database_fail_with_ALB0016()
    {
        // Each would run its own control loops over the same events, so everything is processed
        // twice. It is also exactly what a copy-pasted connection string looks like.
        var definition = Sharded(new TenancyDefinition
        {
            Shards =
            [
                new ShardDeclaration("db1", Postgres(Db1)),
                new ShardDeclaration("db2", Postgres(Db1)),
            ],
            Catalog = Postgres(Catalog),
        });

        Codes(definition).Should().Contain("ALB0016");
    }

    [Fact]
    public void Two_shards_in_separate_schemas_of_one_database_are_accepted()
    {
        var definition = Sharded(new TenancyDefinition
        {
            Shards =
            [
                new ShardDeclaration("db1", Postgres(Db1, schema: "shard_one")),
                new ShardDeclaration("db2", Postgres(Db1, schema: "shard_two")),
            ],
            Catalog = Postgres(Catalog),
        });

        Codes(definition).Should().NotContain("ALB0016");
    }

    [Fact]
    public void A_shards_own_backend_problems_are_reported_against_that_shard()
    {
        var definition = Sharded(new TenancyDefinition
        {
            Shards =
            [
                new ShardDeclaration("db1", Postgres(Db1)),
                new ShardDeclaration("db2", new PostgresBackendDescriptor(
                    new PostgresOptions { ConnectionString = Db2, Schema = "orders; DROP TABLE" })),
            ],
            Catalog = Postgres(Catalog),
        });

        new AlbertoModuleValidator().Collect(definition)
            .Should().Contain(f => f.Problem.StartsWith("Shard 'db2':", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_sharding_problem_is_reported_in_one_pass()
    {
        // One restart has to surface the whole list; fixing them one per deploy is the failure
        // mode this validator exists to avoid.
        var definition = Sharded(
            new TenancyDefinition
            {
                Shards =
                [
                    new ShardDeclaration("DB-ONE", Postgres(Db1)),
                    new ShardDeclaration("DB-ONE", Postgres(Db1)),
                ],
                DefaultShardId = "db9",
            },
            tenancyEnabled: false);

        Codes(definition).Should().Contain(["ALB0010", "ALB0012", "ALB0013", "ALB0014", "ALB0016"]);
    }

    [Fact]
    public void Sharding_problems_are_reported_once_and_not_once_per_shard()
    {
        // The per-shard copies carry an empty tenancy declaration, which is what stops the shard
        // loop in Collect from re-reporting the module's own problems.
        var definition = Sharded(new TenancyDefinition
        {
            Shards =
            [
                new ShardDeclaration("db1", Postgres(Db1)),
                new ShardDeclaration("db2", Postgres(Db2)),
                new ShardDeclaration("db3", Postgres("Host=db3;Database=alberto;Username=x;Password=y")),
            ],
            Catalog = Postgres(Catalog),
            DefaultShardId = "db9",
        });

        Codes(definition).Should().ContainSingle().Which.Should().Be("ALB0013");
    }

    [Fact]
    public void UndeclaredConfiguredShards_is_reported_even_when_the_module_is_not_sharded()
    {
        // A module that dropped its last .AddShard(...) but kept the configuration section still
        // needs to hear about it.
        var definition = Sharded(new TenancyDefinition
        {
            UndeclaredConfiguredShards = ImmutableArray.Create("db1"),
        });

        Codes(definition).Should().ContainSingle().Which.Should().Be("ALB0015");
    }
}
