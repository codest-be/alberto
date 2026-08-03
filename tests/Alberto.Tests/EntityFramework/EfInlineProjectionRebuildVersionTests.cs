using Alberto.EntityFramework.Inline;
using Alberto.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.EntityFramework;

/// <summary>
/// EF-3: the inline EF projection has to read and write one rebuild version, not all of them.
/// </summary>
/// <remarks>
/// The combination that breaks is documented on the projection itself: the same entity
/// registered both inline and async, with a rebuild of the async side in flight. The table
/// then holds <c>(docId, v1)</c> and <c>(docId, v2)</c>, and an unfiltered load hands
/// <c>ToDictionaryAsync(e =&gt; e.DocumentId)</c> two rows with the same key. On the inline
/// path the resulting <see cref="ArgumentException"/> comes out of the caller's
/// <c>AppendAsync</c>, so the failure is not a lagging read model — it is every write to the
/// module failing, for as long as the rebuild runs.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class EfInlineProjectionRebuildVersionTests(EfProjectionTestFixture fixture)
    : IClassFixture<EfProjectionTestFixture>
{
    [Fact]
    public async Task ProcessAsync_WithAShadowRebuildRowForTheSameDocument_DoesNotThrow()
    {
        var entityId = Guid.NewGuid().ToString();

        // What a shadow rebuild loop leaves behind while it replays: the same document at a
        // version nothing is reading yet.
        await fixture.SeedAsync(new CounterEntity
        {
            DocumentId = entityId,
            RebuildVersion = 2,
            Counter = 999,
            LastProcessedPosition = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            EfProjectionTestFixture.BuildDeclaration("rebuild-window-live"),
            fixture.CreateFactory());

        var act = async () => await projection.ProcessAsync(
            [EfTestEnvelopes.ForCounterIncremented(entityId, amount: 7, position: 1)],
            ct: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();

        var versions = await fixture.ReadVersionsAsync(entityId);
        versions.Should().ContainKey(ProjectionVersions.Initial);
        versions[ProjectionVersions.Initial].Counter.Should().Be(7);

        // The shadow row is the rebuild's business, not the live projection's.
        versions[2].Counter.Should().Be(999);
    }

    [Fact]
    public async Task ProcessAsync_FoldsTheRowForItsOwnVersionRatherThanWhicheverCameBack()
    {
        var entityId = Guid.NewGuid().ToString();

        await fixture.SeedAsync(new CounterEntity
        {
            DocumentId = entityId,
            RebuildVersion = ProjectionVersions.Initial,
            Counter = 100,
            LastProcessedPosition = 5,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // A projection pinned to version 2 must not fold the version 1 row's counter, even
        // though that is the row a document-id-only query would have returned first.
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            EfProjectionTestFixture.BuildDeclaration("rebuild-window-shadow"),
            fixture.CreateFactory(),
            rebuildVersion: () => 2);

        await projection.ProcessAsync(
            [EfTestEnvelopes.ForCounterIncremented(entityId, amount: 7, position: 10)],
            ct: TestContext.Current.CancellationToken);

        var versions = await fixture.ReadVersionsAsync(entityId);
        versions[2].Counter.Should().Be(7);
        versions[ProjectionVersions.Initial].Counter.Should().Be(100);
    }

    [Fact]
    public async Task ProcessAsync_StampsNewRowsWithTheResolvedVersion()
    {
        var entityId = Guid.NewGuid().ToString();

        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            EfProjectionTestFixture.BuildDeclaration("rebuild-window-insert"),
            fixture.CreateFactory(),
            rebuildVersion: () => 3);

        await projection.ProcessAsync(
            [EfTestEnvelopes.ForCounterIncremented(entityId, amount: 4, position: 1)],
            ct: TestContext.Current.CancellationToken);

        // Without an explicit stamp this row lands on the column default (1) instead, which
        // reads correctly only for as long as the live version happens to be 1.
        var versions = await fixture.ReadVersionsAsync(entityId);
        versions.Keys.Should().Equal(3);
        versions[3].Counter.Should().Be(4);
    }
}
