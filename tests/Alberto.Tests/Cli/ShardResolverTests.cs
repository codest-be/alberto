using Alberto.Cli;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Cli;

/// <summary>
/// Which databases an operator command lands in, decided from the config alone.
/// </summary>
/// <remarks>
/// These go through the overloads that take an <see cref="AlbertoConfig"/> rather than reading
/// <c>.alberto/config.json</c>, so nothing here depends on the working directory.
/// </remarks>
public class ShardResolverTests
{
    private static AlbertoConfig Sharded() => new()
    {
        Schema = "events",
        Shards = new Dictionary<string, ShardConfig>
        {
            ["db2"] = new() { Url = "Host=two" },
            ["db1"] = new() { Url = "Host=one" },
        },
    };

    [Fact]
    public void A_module_with_no_shards_resolves_to_one_unnamed_database()
    {
        var targets = ShardResolver.ResolveForRead(new AlbertoConfig(), null, "Host=only", null);

        targets.Should().ContainSingle();
        targets[0].ShardId.Should().BeNull("unsharded output must look exactly as it did before shards existed");
        targets[0].ConnectionString.Should().Be("Host=only");
    }

    [Fact]
    public void A_module_with_no_shards_lets_a_mutation_through_without_a_selection()
    {
        var targets = ShardResolver.ResolveForMutation(new AlbertoConfig(), null, false, "Host=only", null);

        targets.Should().ContainSingle();
        targets[0].ShardId.Should().BeNull();
    }

    [Fact]
    public void A_read_with_no_shard_covers_every_database_in_id_order()
    {
        var targets = ShardResolver.ResolveForRead(Sharded(), null, null, null);

        targets.Select(t => t.ShardId).Should().Equal("db1", "db2");
        targets.Select(t => t.ConnectionString).Should().Equal("Host=one", "Host=two");
    }

    [Fact]
    public void A_read_naming_a_shard_covers_only_that_one()
    {
        var targets = ShardResolver.ResolveForRead(Sharded(), "db2", null, null);

        targets.Should().ContainSingle();
        targets[0].ShardId.Should().Be("db2");
    }

    [Fact]
    public void Naming_a_shard_that_is_not_configured_says_which_ones_are()
    {
        var resolve = () => ShardResolver.ResolveForRead(Sharded(), "db9", null, null);

        resolve.Should().Throw<ShardSelectionException>()
            .WithMessage("*db9*").And.Message.Should().Contain("db1").And.Contain("db2");
    }

    [Fact]
    public void A_mutation_with_no_selection_refuses_and_names_the_databases_it_would_have_touched()
    {
        var resolve = () => ShardResolver.ResolveForMutation(Sharded(), null, false, null, null);

        resolve.Should().Throw<ShardSelectionException>()
            .Which.Message.Should().Contain("db1").And.Contain("db2").And.Contain("--all-shards");
    }

    [Fact]
    public void A_mutation_that_cannot_span_databases_does_not_offer_all_shards()
    {
        var resolve = () => ShardResolver.ResolveForMutation(
            Sharded(), null, false, null, null, supportsAllShards: false);

        resolve.Should().Throw<ShardSelectionException>()
            .Which.Message.Should().NotContain("or --all-shards").And.Contain("per-database sequence");
    }

    [Fact]
    public void A_mutation_asking_for_every_shard_gets_every_shard()
    {
        var targets = ShardResolver.ResolveForMutation(Sharded(), null, true, null, null);

        targets.Select(t => t.ShardId).Should().Equal("db1", "db2");
    }

    [Fact]
    public void An_explicit_url_beats_the_configured_shards()
    {
        // The operator has named a database. Fanning out over the config's shards while ignoring
        // what they typed would be a surprise, and a destructive one for a mutation.
        var targets = ShardResolver.ResolveForMutation(Sharded(), null, false, "Host=elsewhere", null);

        targets.Should().ContainSingle();
        targets[0].ShardId.Should().BeNull();
        targets[0].ConnectionString.Should().Be("Host=elsewhere");
    }

    [Fact]
    public void A_shards_own_schema_beats_the_modules()
    {
        var config = Sharded();
        config.Shards!["db1"].Schema = "db1_events";

        var targets = ShardResolver.ResolveForRead(config, null, null, null);

        targets.Single(t => t.ShardId == "db1").Schema.Should().Be("db1_events");
        targets.Single(t => t.ShardId == "db2").Schema.Should().Be("events");
    }

    [Fact]
    public void The_command_line_schema_beats_every_configured_one()
    {
        var config = Sharded();
        config.Shards!["db1"].Schema = "db1_events";

        var targets = ShardResolver.ResolveForRead(config, null, null, "override");

        targets.Should().OnlyContain(t => t.Schema == "override");
    }

    [Fact]
    public void A_shard_with_no_url_invalidates_the_whole_shard_map()
    {
        var config = Sharded();
        config.Shards!["db3"] = new ShardConfig();

        var declare = () => ShardResolver.DeclaredShardIds(config);
        var resolve = () => ShardResolver.ResolveForRead(config, null, null, null);

        declare.Should().Throw<ShardSelectionException>()
            .Which.Message.Should().Contain("db3").And.Contain("No shard was selected");
        resolve.Should().Throw<ShardSelectionException>()
            .Which.Message.Should().Contain("db3").And.Contain("No shard was selected");
    }

    [Fact]
    public void A_module_with_no_shards_declares_none()
    {
        ShardResolver.DeclaredShardIds(new AlbertoConfig()).Should().BeEmpty();
    }

    [Fact]
    public void The_catalog_falls_back_to_the_modules_schema()
    {
        var config = Sharded();
        config.Catalog = new CatalogConfig { Url = "Host=control" };

        var catalog = ShardResolver.ResolveCatalog(config, null, null);

        catalog.ConnectionString.Should().Be("Host=control");
        catalog.Schema.Should().Be("events");
    }

    [Fact]
    public void A_missing_catalog_says_how_to_configure_one()
    {
        var resolve = () => ShardResolver.ResolveCatalog(Sharded(), null, null);

        resolve.Should().Throw<ShardSelectionException>()
            .Which.Message.Should().Contain("catalog").And.Contain("--url");
    }

    [Fact]
    public void The_module_key_comes_from_the_config_and_the_option_overrides_it()
    {
        var config = new AlbertoConfig { Module = "orders" };

        ShardResolver.ResolveModuleKey(config, null).Should().Be("orders");
        ShardResolver.ResolveModuleKey(config, "payments").Should().Be("payments");
    }

    [Fact]
    public void A_missing_module_key_is_refused_rather_than_guessed()
    {
        // Guessing would read an empty catalog table and report no tenants, which looks like an
        // answer rather than a misconfiguration.
        var resolve = () => ShardResolver.ResolveModuleKey(new AlbertoConfig(), null);

        resolve.Should().Throw<ShardSelectionException>()
            .Which.Message.Should().Contain("module");
    }
}
