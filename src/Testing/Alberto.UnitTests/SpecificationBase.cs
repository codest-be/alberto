using Alberto.EventSourcing;
using Xunit;

namespace Alberto.UnitTests;

public abstract class SpecificationBase
{
    private Decision? _decision;
    private object? _resultValue;

    protected void SetDecision(Decision decision, object? resultValue = null)
    {
        _decision = decision;
        _resultValue = resultValue;
    }

    public SpecificationBase ThenSucceed()
    {
        Assert.True(
            _decision?.IsSuccess,
            $"Expected decision to succeed, but it failed with: {string.Join(",", _decision!.Value.Problems.Select(x => x.Code))}");

        return this;
    }

    public SpecificationBase ThenFail()
    {
        Assert.True(
            _decision?.IsError,
            "Expected decision to fail, but it succeeded");

        return this;
    }

    public SpecificationBase ThenFailWith(Problem expectedProblem)
    {
        Assert.True(
            _decision?.IsError,
            "Expected decision to fail, but it succeeded");

        Assert.Contains(expectedProblem.Code, _decision!.Value.Problems.Select(x => x.Code));

        return this;
    }

    public SpecificationBase ThenEvents(Action<IReadOnlyList<object>> eventsAssertion)
    {
        eventsAssertion(_decision?.Events ?? []);
        return this;
    }

    public SpecificationBase ThenEventOfType<TEvent>()
    {
        Assert.Contains(_decision?.Events ?? [], e => e is TEvent);
        return this;
    }

    public SpecificationBase ThenEventOfType<TEvent>(Func<TEvent, bool> predicate)
    {
        Assert.Contains(_decision?.Events ?? [], e => e is TEvent evt && predicate(evt));
        return this;
    }

    public SpecificationBase ThenResult<TResult>(Action<TResult?> resultAssertion)
    {
        if (_resultValue is TResult result)
        {
            resultAssertion(result);
        }
        else
        {
            resultAssertion(default);
        }

        return this;
    }
}