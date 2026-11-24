using System.Text.Json;
using Alberto.EntityFramework;
using Alberto.EntityFramework.Inline;
using Alberto.Postgres;
using Alberto.Projections;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Alberto.Tests.EntityFramework;

[EventType("shelf-stocked")]
public record ShelfStocked(string ShelfId, string Label, string City, string[] Skus) : IEvent;

[EventType("shelf-moved")]
public record ShelfMoved(string ShelfId, string City, string[] Skus) : IEvent;

[EventType("shelf-relabeled")]
public record ShelfRelabeled(string ShelfId, string Label) : IEvent;

[EventType("shelf-removed")]
public record ShelfRemoved(string ShelfId) : IEvent;

/// <summary>
/// An inline projection of an entity with an owned type and an owned collection, appended through
/// a real <see cref="EventStore"/>: the owned rows must follow the document through an update and
/// a delete, in whichever way the projection stages its writes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfInlineProjectionOwnedGraphTests(EfEventStoreFixture fixture)
    : IClassFixture<EfEventStoreFixture>
{
    [Fact]
    public async Task ASecondEvent_ReplacesTheOwnedTypeAndTheOwnedCollection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = NewStore("owned-update");
        var shelfId = Guid.NewGuid().ToString();

        await store.AppendAsync([Stocked(shelfId, "Ghent", "a", "b")], cancellationToken: cancellationToken);
        await store.AppendAsync([Moved(shelfId, "Bruges", "c")], cancellationToken: cancellationToken);

        var shelf = await ReadShelfAsync(shelfId);
        Assert.NotNull(shelf);
        Assert.Equal("shelf", shelf!.Label);
        Assert.Equal("Bruges", shelf.Address.City);
        Assert.Equal(new[] { "c" }, shelf.Items.Select(i => i.Sku));
        Assert.Equal(1, await CountItemRowsAsync(shelfId));
    }

    [Fact]
    public async Task AnAppendThatDeletesAnExistingDocument_RemovesItsOwnedRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = NewStore("owned-delete");
        var shelfId = Guid.NewGuid().ToString();
        await store.AppendAsync([Stocked(shelfId, "Ghent", "a", "b")], cancellationToken: cancellationToken);

        await store.AppendAsync([Removed(shelfId)], cancellationToken: cancellationToken);

        Assert.Null(await ReadShelfAsync(shelfId));
        Assert.Equal(0, await CountItemRowsAsync(shelfId));
    }

    [Fact]
    public async Task AnAppendThatCreatesAndDeletesADocument_LeavesNothingBehind()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = NewStore("owned-create-delete");
        var shelfId = Guid.NewGuid().ToString();

        await store.AppendAsync(
            [Stocked(shelfId, "Ghent", "a"), Removed(shelfId)], cancellationToken: cancellationToken);

        Assert.Null(await ReadShelfAsync(shelfId));
        Assert.Equal(0, await CountItemRowsAsync(shelfId));
    }

    [Fact]
    public async Task AnAppendThatDeletesAndRecreatesADocument_KeepsOnlyTheNewOwnedRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = NewStore("owned-recreate");
        var shelfId = Guid.NewGuid().ToString();
        await store.AppendAsync([Stocked(shelfId, "Ghent", "a", "b")], cancellationToken: cancellationToken);

        await store.AppendAsync(
            [Removed(shelfId), Stocked(shelfId, "Bruges", "z")], cancellationToken: cancellationToken);

        var shelf = await ReadShelfAsync(shelfId);
        Assert.Equal("Bruges", shelf!.Address.City);
        Assert.Equal(new[] { "z" }, shelf.Items.Select(i => i.Sku));
        Assert.Equal(1, await CountItemRowsAsync(shelfId));
    }

    [Fact]
    public async Task APostCommitProjection_ReplacesTheOwnedRowsToo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projection = new DeclaredEfInlineProjection<ShelfEntity, EfTestDbContext>(
            BuildDeclaration("owned-after-commit"), fixture.CreateFactory());
        var shelfId = Guid.NewGuid().ToString();

        await projection.ProcessAsync([Envelope(new ShelfStocked(shelfId, "shelf", "Ghent", ["a", "b"]), 1)], AppendTransaction.None, cancellationToken);
        await projection.ProcessAsync([Envelope(new ShelfMoved(shelfId, "Bruges", ["c"]), 2)], AppendTransaction.None, cancellationToken);

        Assert.Equal(new[] { "c" }, (await ReadShelfAsync(shelfId))!.Items.Select(i => i.Sku));
        Assert.Equal(1, await CountItemRowsAsync(shelfId));
    }

    [Fact]
    public async Task AHandlerThatChangesALoadedDocumentInPlaceWithoutWritingIt_DoesNotPersistTheChange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = NewStore("owned-in-place");
        var shelfId = Guid.NewGuid().ToString();
        var otherId = Guid.NewGuid().ToString();
        await store.AppendAsync([Stocked(shelfId, "Ghent", "a")], cancellationToken: cancellationToken);

        await store.AppendAsync(
            [Relabeled(shelfId, "scribbled"), Stocked(otherId, "Bruges", "z")], cancellationToken: cancellationToken);

        Assert.Equal("shelf", (await ReadShelfAsync(shelfId))!.Label);
        Assert.NotNull(await ReadShelfAsync(otherId));
    }

    private EventStore NewStore(string processorId) => new(
        new PostgresEventStoreBackend(fixture.DataSource),
        [new DeclaredEfInlineProjection<ShelfEntity, EfTestDbContext>(BuildDeclaration(processorId), fixture.CreateFactory())]);

    private static ProjectionDeclaration<ShelfEntity> BuildDeclaration(string processorId) =>
        DeclareProjection.For<ShelfEntity>(processorId)
            .On<ShelfStocked>(
                id: e => e.ShelfId,
                apply: (_, e, _) => new ShelfEntity
                {
                    Label = "shelf",
                    Address = new ShelfAddress { City = e.City },
                    Items = e.Skus.Select(sku => new ShelfItem { Sku = sku, Quantity = 1 }).ToList(),
                })
            .On<ShelfMoved>(
                id: e => e.ShelfId,
                apply: (state, e, _) => new ShelfEntity
                {
                    Label = state.Label,
                    Address = new ShelfAddress { City = e.City },
                    Items = e.Skus.Select(sku => new ShelfItem { Sku = sku, Quantity = 1 }).ToList(),
                })
            .On<ShelfRelabeled>(
                id: e => e.ShelfId,
                apply: (state, e, _) =>
                {
                    state.Label = e.Label;
                    return ProjectionResults.Unchanged<ShelfEntity>();
                })
            .On<ShelfRemoved>(
                id: e => e.ShelfId,
                apply: (_, _, _) => ProjectionResults.Delete<ShelfEntity>())
            .Build();

    private async Task<ShelfEntity?> ReadShelfAsync(string shelfId)
    {
        await using var context = await fixture.CreateFactory().CreateDbContextAsync();
        return await context.Shelves.AsNoTracking()
            .IgnoreQueryFilters([ProjectionQueryFilters.ActiveVersion])
            .FirstOrDefaultAsync(s => s.DocumentId == shelfId);
    }

    private async Task<int> CountItemRowsAsync(string shelfId)
    {
        await using var context = await fixture.CreateFactory().CreateDbContextAsync();
        return await context.Database
            .SqlQuery<int>($"select count(*)::int as \"Value\" from ef_test_shelf_items where \"ShelfEntityDocumentId\" = {shelfId}")
            .SingleAsync();
    }

    private static EventToPersist Stocked(string shelfId, string city, params string[] skus) => new()
    {
        EventType = new EventType("shelf-stocked"),
        Tags = [],
        EventData = JsonSerializer.Serialize(new ShelfStocked(shelfId, "shelf", city, skus)),
    };

    private static EventToPersist Moved(string shelfId, string city, params string[] skus) => new()
    {
        EventType = new EventType("shelf-moved"),
        Tags = [],
        EventData = JsonSerializer.Serialize(new ShelfMoved(shelfId, city, skus)),
    };

    private static EventToPersist Relabeled(string shelfId, string label) => new()
    {
        EventType = new EventType("shelf-relabeled"),
        Tags = [],
        EventData = JsonSerializer.Serialize(new ShelfRelabeled(shelfId, label)),
    };

    private static EventToPersist Removed(string shelfId) => new()
    {
        EventType = new EventType("shelf-removed"),
        Tags = [],
        EventData = JsonSerializer.Serialize(new ShelfRemoved(shelfId)),
    };

    private static IEventEnvelope Envelope(IEvent evt, long position) => new EventEnvelope
    {
        Id = Guid.NewGuid(),
        TenantId = "test-tenant",
        GlobalPosition = position,
        EventType = new EventType(evt switch
        {
            ShelfStocked => "shelf-stocked",
            ShelfMoved => "shelf-moved",
            _ => "shelf-removed",
        }),
        Tags = [],
        EventData = JsonSerializer.Serialize(evt, evt.GetType()),
        Metadata = new Dictionary<string, string>(),
        CreatedAt = DateTime.UtcNow,
    };
}
