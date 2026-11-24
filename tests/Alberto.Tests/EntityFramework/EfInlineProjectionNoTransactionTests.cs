using Alberto.Projections;
using Alberto.EntityFramework.Inline;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.EntityFramework;

/// <summary>
/// A backend with no database transaction (the in-memory backend) hands the projection
/// <see cref="AppendTransaction.None"/>; the projection then writes through a context of its own.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfInlineProjectionNoTransactionTests(EfProjectionTestFixture fixture)
    : IClassFixture<EfProjectionTestFixture>
{
    [Fact]
    public async Task ProcessInTransactionAsync_WithNoTransaction_WritesThroughAContextOfItsOwn()
    {
        var entityId = Guid.NewGuid().ToString();
        var projection = new DeclaredEfInlineProjection<CounterEntity, EfTestDbContext>(
            EfProjectionTestFixture.BuildDeclaration("no-transaction"),
            fixture.CreateFactory());

        await projection.ProcessInTransactionAsync(
            [EfTestEnvelopes.ForCounterIncremented(entityId, amount: 3, position: 1)],
            AppendTransaction.None,
            TestContext.Current.CancellationToken);
        await projection.ProcessInTransactionAsync(
            [EfTestEnvelopes.ForCounterIncremented(entityId, amount: 4, position: 2)],
            AppendTransaction.None,
            TestContext.Current.CancellationToken);

        var versions = await fixture.ReadVersionsAsync(entityId);
        versions[ProjectionVersions.Initial].Counter.Should().Be(7);
    }
}
