using Xunit;

namespace Alberto.Dcb.Tests;

public class DecisionResultTests
{
    [EventType("order-created")]
    private record OrderCreated(string OrderId) : IEvent;

    [Fact]
    public void Success_IsSuccessTrue()
    {
        var result = DecisionResult<OrderCreated>.Success(new OrderCreated("123"));
        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public void Failure_IsFailureTrue()
    {
        var result = DecisionResult<OrderCreated>.Failure("order already exists");
        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void EnsureSuccess_ReturnsEvents_WhenSuccess()
    {
        var evt = new OrderCreated("123");
        var result = DecisionResult<OrderCreated>.Success(evt);
        var events = result.EnsureSuccess();
        Assert.Single(events);
        Assert.Equal(evt, events[0]);
    }

    [Fact]
    public void EnsureSuccess_Throws_WhenFailure()
    {
        var result = DecisionResult<OrderCreated>.Failure("not allowed");
        Assert.Throws<InvalidOperationException>(() => result.EnsureSuccess());
    }

    [Fact]
    public void PatternMatch_WorksCorrectly()
    {
        var result = DecisionResult<OrderCreated>.Success(new OrderCreated("456"));
        var message = result switch
        {
            DecisionResult<OrderCreated>.Ok ok => $"OK with {ok.Events.Count} event(s)",
            DecisionResult<OrderCreated>.Fail fail => $"Fail: {fail.Reason}",
            _ => "unknown"
        };
        Assert.Equal("OK with 1 event(s)", message);
    }
}
