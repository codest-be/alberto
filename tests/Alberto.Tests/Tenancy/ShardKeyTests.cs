using Alberto.Tenancy;
using Xunit;

namespace Alberto.Tests.Tenancy;

public class ShardKeyTests
{
    [Fact]
    public void Compose_joins_the_module_and_the_shard()
    {
        Assert.Equal("orders#db1", ShardKey.Compose("orders", "db1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Compose_rejects_a_missing_module_key(string? moduleKey)
    {
        var compose = () => ShardKey.Compose(moduleKey!, "db1");

        Assert.ThrowsAny<ArgumentException>(compose);
    }

    [Fact]
    public void A_composed_key_round_trips()
    {
        Assert.True(ShardKey.TryParse(ShardKey.Compose("orders", "db1"), out var moduleKey, out var shardId));

        Assert.Equal("orders", moduleKey);
        Assert.Equal("db1", shardId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("orders")]        // an unsharded module key
    [InlineData("#db1")]          // no module
    [InlineData("orders#")]       // no shard
    [InlineData("orders#db1#x")]  // never produced by Compose
    public void TryParse_refuses_anything_Compose_did_not_produce(string? key)
    {
        Assert.False(ShardKey.TryParse(key, out var moduleKey, out var shardId));

        Assert.Null(moduleKey);
        Assert.Null(shardId);
    }

    [Fact]
    public void ModuleOf_returns_an_unsharded_key_unchanged()
    {
        // This is what keeps every existing consumer of ModuleKey working: an unsharded module
        // key is its own logical key.
        Assert.Equal("orders", ShardKey.ModuleOf("orders"));
        Assert.Null(ShardKey.ShardOf("orders"));
    }

    [Fact]
    public void ModuleOf_and_ShardOf_split_a_shard_key()
    {
        Assert.Equal("orders", ShardKey.ModuleOf("orders#db1"));
        Assert.Equal("db1", ShardKey.ShardOf("orders#db1"));
    }

    [Theory]
    [InlineData("db1")]
    [InlineData("eu_west")]
    [InlineData("a")]
    public void Valid_shard_ids_are_accepted(string shardId)
    {
        Assert.True(ShardKey.IsValidShardId(shardId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DB1")]        // uppercase
    [InlineData("1db")]        // leading digit
    [InlineData("eu-west")]    // hyphen
    [InlineData("db 1")]       // space
    [InlineData("db;drop")]    // would need quoting
    [InlineData("orders#db1")] // the separator itself
    public void Unsafe_shard_ids_are_rejected(string? shardId)
    {
        Assert.False(ShardKey.IsValidShardId(shardId));
    }

    [Fact]
    public void A_shard_id_is_capped_at_63_characters()
    {
        Assert.True(ShardKey.IsValidShardId('a' + new string('b', 62)));
        Assert.False(ShardKey.IsValidShardId('a' + new string('b', 63)));
    }
}
