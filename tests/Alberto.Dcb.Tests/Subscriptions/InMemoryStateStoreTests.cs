using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// The in-memory state store is what samples and tests project into, so it has to honour the
/// same <see cref="IStateStore{TState}"/> contract the durable stores do — including keying
/// state by rebuild version, which is the part a naive dictionary implementation gets wrong.
/// </summary>
public sealed class InMemoryStateStoreTests
{
    private sealed record Counter
    {
        public int Value { get; init; }
    }

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task ApplyChanges_upserts_and_deletes()
    {
        var store = new InMemoryStateStore<Counter>();

        await store.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["a"] = new() { Value = 1 }, ["b"] = new() { Value = 2 } },
            [],
            ct: Ct);

        (await store.LoadManyAsync(["a", "b"], ct: Ct))
            .Should().BeEquivalentTo(new Dictionary<string, Counter>
            {
                ["a"] = new() { Value = 1 },
                ["b"] = new() { Value = 2 }
            });

        await store.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["a"] = new() { Value = 3 } },
            ["b"],
            ct: Ct);

        var after = await store.LoadManyAsync(["a", "b"], ct: Ct);
        after.Should().ContainKey("a").WhoseValue.Value.Should().Be(3);
        after.Should().NotContainKey("b");
    }

    [Fact]
    public async Task LoadMany_skips_documents_that_do_not_exist()
    {
        var store = new InMemoryStateStore<Counter>();

        var loaded = await store.LoadManyAsync(["missing"], ct: Ct);

        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task State_written_under_one_rebuild_version_is_invisible_to_another()
    {
        var version = ProjectionVersions.Initial;
        var store = new InMemoryStateStore<Counter>(() => version);

        await store.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["a"] = new() { Value = 1 } },
            [],
            ct: Ct);

        version = 2;

        // A shadow rebuild replaying into version 2 must start from nothing, not inherit the
        // live version's rows.
        (await store.LoadManyAsync(["a"], ct: Ct)).Should().BeEmpty();

        await store.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["a"] = new() { Value = 99 } },
            [],
            ct: Ct);

        (await store.LoadManyAsync(["a"], ct: Ct))["a"].Value.Should().Be(99);

        // ...and promoting is what makes it visible; the old version is still there underneath.
        version = ProjectionVersions.Initial;
        (await store.LoadManyAsync(["a"], ct: Ct))["a"].Value.Should().Be(1);
    }

    [Fact]
    public async Task Deletes_only_affect_the_current_rebuild_version()
    {
        var version = ProjectionVersions.Initial;
        var store = new InMemoryStateStore<Counter>(() => version);

        await store.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["a"] = new() { Value = 1 } }, [], ct: Ct);

        version = 2;
        await store.ApplyChangesAsync(
            new Dictionary<string, Counter> { ["a"] = new() { Value = 2 } }, [], ct: Ct);
        await store.ApplyChangesAsync(new Dictionary<string, Counter>(), ["a"], ct: Ct);

        (await store.LoadManyAsync(["a"], ct: Ct)).Should().BeEmpty();

        version = ProjectionVersions.Initial;
        (await store.LoadManyAsync(["a"], ct: Ct))["a"].Value.Should().Be(1);
    }
}
