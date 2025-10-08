using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventStore.InMemory;
using Xunit;

namespace Alberto.Example.ComponentTests;

public sealed class CommandAsserter
{
    private readonly InMemoryEventStoreBackend _eventStore;
    private readonly Guid[] _givenEventIds;
    private readonly object _result;

    internal CommandAsserter(object result, Guid[] givenEventIds, InMemoryEventStoreBackend eventStore)
    {
        _result = result;
        _givenEventIds = givenEventIds;
        _eventStore = eventStore;
    }

    public void ThenExpectSuccess(Action<EventAsserter> assert)
    {
        // Try to get IsSuccess and Problems via reflection to support both Result and Result<T>
        var resultType = _result.GetType();

        var isSuccessProperty = resultType.GetProperty("IsSuccess");
        var problemsProperty = resultType.GetProperty("Problems");

        if (isSuccessProperty == null || problemsProperty == null)
        {
            throw new InvalidOperationException(
                $"Result type {resultType.Name} does not have IsSuccess or Problems properties");
        }

        var isSuccess = (bool)isSuccessProperty.GetValue(_result)!;
        var problems = (IReadOnlyList<Problem>)problemsProperty.GetValue(_result)!;

        Assert.True(isSuccess,
            $"Expected success but got failure: {string.Join(", ", problems.Select(p => $"{p.Code}: {p.Message}"))}");

        assert(new EventAsserter(_eventStore, _givenEventIds));
    }

    public void ThenExpectSuccess()
    {
        ThenExpectSuccess(_ => { });
    }

    public void ThenExpectSuccess<T>(Action<T, EventAsserter> assert)
    {
        if (_result is not Result<T> result)
        {
            throw new InvalidOperationException($"Result is not of type Result<{typeof(T).Name}>");
        }

        Assert.True(result.IsSuccess,
            $"Expected success but got failure: {string.Join(", ", result.Problems.Select(p => $"{p.Code}: {p.Message}"))}");

        assert(result.Value, new EventAsserter(_eventStore, _givenEventIds));
    }

    public void ThenExpectFailure(params string[] problemCodes)
    {
        // Try to get IsSuccess and Problems via reflection to support both Result and Result<T>
        var resultType = _result.GetType();

        var isSuccessProperty = resultType.GetProperty("IsSuccess");
        var problemsProperty = resultType.GetProperty("Problems");

        if (isSuccessProperty == null || problemsProperty == null)
        {
            throw new InvalidOperationException(
                $"Result type {resultType.Name} does not have IsSuccess or Problems properties");
        }

        var isSuccess = (bool)isSuccessProperty.GetValue(_result)!;
        var problems = (IReadOnlyList<Problem>)problemsProperty.GetValue(_result)!;

        Assert.False(isSuccess, "Expected failure but got success");

        foreach (var expectedCode in problemCodes)
        {
            Assert.Contains(expectedCode, problems.Select(p => p.Code));
        }

        if (problemCodes.Length > 0)
        {
            Assert.Equal(problemCodes.Length, problems.Count);
        }
    }
}