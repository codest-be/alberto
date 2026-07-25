using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Tests for the blessed <see cref="Decision"/> and <see cref="Decision{T}"/> types
/// that replaced the former DecisionResult&lt;TEvent&gt;.
/// </summary>
public class DecisionTests
{
    [EventType("decision-test-order-created")]
    private record OrderCreated(string OrderId) : IEvent;

    // ── Decision (non-generic) ───────────────────────────────────────────

    [Fact]
    public void Decision_Succeed_WithEvent_IsSuccess()
    {
        var result = Decision.Succeed(new OrderCreated("123"));
        Assert.True(result.IsSuccess);
        Assert.False(result.IsError);
        Assert.Single(result.Events);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Decision_Succeed_NoEvents_IsSuccess()
    {
        var result = Decision.Succeed();
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void Decision_Fail_WithProblem_IsError()
    {
        var problem = Problem.Create("not-allowed", "Operation not allowed");
        var result = Decision.Fail(problem);
        Assert.False(result.IsSuccess);
        Assert.True(result.IsError);
        Assert.Single(result.Problems);
        Assert.Equal("not-allowed", result.Problems[0].Code);
    }

    [Fact]
    public void Decision_Fail_WithMultipleProblems_AllCaptured()
    {
        var problems = new[]
        {
            Problem.Create("err-1", "First error"),
            Problem.Create("err-2", "Second error")
        };
        var result = Decision.Fail(problems);
        Assert.True(result.IsError);
        Assert.Equal(2, result.Problems.Count);
    }

    [Fact]
    public void Decision_ImplicitFromProblem_IsError()
    {
        Decision result = Problem.Create("implicit", "Implicit problem");
        Assert.True(result.IsError);
        Assert.Single(result.Problems);
    }

    // ── Decision<T> (generic, value-carrying) ────────────────────────────

    [Fact]
    public void DecisionT_Succeed_ValueAndEventsAccessible()
    {
        var evt = new OrderCreated("456");
        var result = Decision<string>.Succeed("hello", evt);
        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Value);
        Assert.Single(result.Events);
    }

    [Fact]
    public void DecisionT_Succeed_WithEnumerableEvents_Captured()
    {
        IEnumerable<IEvent> events = [new OrderCreated("1"), new OrderCreated("2")];
        var result = Decision<int>.Succeed(42, events);
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Equal(2, result.Events.Count);
    }

    [Fact]
    public void DecisionT_Fail_ValueThrows()
    {
        var result = Decision<string>.Fail(Problem.Create("err", "Failed"));
        Assert.False(result.IsSuccess);
        Assert.True(result.IsError);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void DecisionT_Map_TransformsValue_WhenSuccess()
    {
        var result = Decision<int>.Succeed(7, new OrderCreated("7"));
        var mapped = result.Map(n => n.ToString());
        Assert.True(mapped.IsSuccess);
        Assert.Equal("7", mapped.Value);
        Assert.Single(mapped.Events);
    }

    [Fact]
    public void DecisionT_Map_PropagatesProblems_WhenFailure()
    {
        var result = Decision<int>.Fail(Problem.Create("e", "Error"));
        var mapped = result.Map(n => n.ToString());
        Assert.True(mapped.IsError);
        Assert.Single(mapped.Problems);
    }

    [Fact]
    public void DecisionT_ImplicitConversionToDecision_PreservesEventsAndProblems()
    {
        Decision<string> typed = Decision<string>.Succeed("val", new OrderCreated("1"));
        Decision untyped = typed;
        Assert.True(untyped.IsSuccess);
        Assert.Single(untyped.Events);

        Decision<string> failed = Decision<string>.Fail(Problem.Create("f", "fail"));
        Decision unfailedTyped = failed;
        Assert.True(unfailedTyped.IsError);
        Assert.Single(unfailedTyped.Problems);
    }

    [Fact]
    public void DecisionT_ImplicitFromProblem_IsError()
    {
        Decision<string> result = Problem.Create("implicit-t", "Implicit problem");
        Assert.True(result.IsError);
        Assert.Single(result.Problems);
    }
}
