using Alberto;
using Alberto.Examples.Shared;
using FluentAssertions;
using HotChocolate;

namespace Alberto.Examples.Tests;

/// <summary>
/// Every slice ends <c>Handle → Load → Decide → Commit → OrThrow</c>. Two things can come back
/// down that chain: a decision that refused, and a boundary that stayed contended for longer than
/// <c>Commit</c>'s retry policy. Only the first is a <see cref="Result"/> failure — the second
/// arrives as an exception, and these tests pin that it still reaches the client as a coded error.
/// </summary>
public class MutationResultsTests
{
    private static DcbConflictException Conflict() =>
        new(conflictingPosition: 42, expectedPosition: 17, DcbQuery.Empty);

    [Fact]
    public async Task OrThrow_CommitThrowsConflict_SurfacesAsCodedGraphQLError()
    {
        var commit = Task.FromException<Result>(Conflict());

        var thrown = await Assert.ThrowsAsync<GraphQLException>(() => commit.OrThrow());

        var error = thrown.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(DcbConflictException.ProblemCode);
        error.Extensions.Should().Contain("expectedPosition", 17L);
        error.Extensions.Should().Contain("conflictingPosition", 42L);
    }

    [Fact]
    public async Task OrThrowOfT_CommitThrowsConflict_SurfacesAsCodedGraphQLError()
    {
        var commit = Task.FromException<Result<string>>(Conflict());

        var thrown = await Assert.ThrowsAsync<GraphQLException>(() => commit.OrThrow());

        var error = thrown.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(DcbConflictException.ProblemCode);
    }

    /// <summary>
    /// The conflict a caught exception produces and the one a <c>TryCommit</c> returns are the same
    /// <see cref="Problem"/>, so a client cannot tell which path a contended boundary took.
    /// </summary>
    [Fact]
    public async Task OrThrow_ThrownConflict_MatchesTheProblemATryCommitWouldReturn()
    {
        var conflict = Conflict();
        var thrown = await Assert.ThrowsAsync<GraphQLException>(
            () => Task.FromException<Result>(conflict).OrThrow());
        var returned = await Assert.ThrowsAsync<GraphQLException>(
            () => Task.FromResult(Result.Fail(conflict.ToProblem())).OrThrow());

        thrown.Errors.Single().Code.Should().Be(returned.Errors.Single().Code);
        thrown.Errors.Single().Message.Should().Be(returned.Errors.Single().Message);
        thrown.Errors.Single().Extensions.Should().BeEquivalentTo(returned.Errors.Single().Extensions);
    }

    [Fact]
    public async Task OrThrow_Refusal_StillCarriesItsOwnCode()
    {
        var commit = Task.FromResult(Result.Fail(Problem.Create("order.already-shipped", "Already shipped.")));

        var thrown = await Assert.ThrowsAsync<GraphQLException>(() => commit.OrThrow());

        thrown.Errors.Single().Code.Should().Be("order.already-shipped");
    }

    /// <summary>
    /// Only conflicts are translated. Anything else is a genuine fault and must not be dressed up
    /// as a domain error the client is invited to retry.
    /// </summary>
    [Fact]
    public async Task OrThrow_OtherException_PropagatesUnchanged()
    {
        var commit = Task.FromException<Result>(new InvalidOperationException("boom"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => commit.OrThrow());

        thrown.Message.Should().Be("boom");
    }

    [Fact]
    public async Task OrThrow_Success_ReturnsTheValue()
    {
        var value = await Task.FromResult(Result<string>.Success("ok")).OrThrow();

        value.Should().Be("ok");
    }
}
