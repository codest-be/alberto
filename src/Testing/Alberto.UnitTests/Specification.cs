using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;

namespace Alberto.UnitTests;

public class Specification<TState> : SpecificationBase where TState : new()
{
    private readonly IProjector<TState> _decider;
    private readonly List<object> _givenEvents = [];
    private TState _finalState = default!;
    private TState _initialState;

    public Specification(IProjector<TState> decider)
    {
        _decider = decider;
        _initialState = new();
    }

    public Specification<TState> Given(params object[] events)
    {
        _givenEvents.AddRange(events);

        _finalState = events.Aggregate(_initialState, _decider.Apply);

        return this;
    }

    public Specification<TState> Given(TState initialState, params object[] events)
    {
        _initialState = initialState;
        _givenEvents.AddRange(events);

        _finalState = events.Aggregate(_initialState, _decider.Apply);

        return this;
    }

    public Specification<TState> When(Func<TState, Decision> decisionFunc)
    {
        var currentState = _givenEvents.Aggregate(_initialState, _decider.Apply);

        var decision = decisionFunc(currentState);
        SetDecision(decision);

        _finalState = decision.IsSuccess ? decision.Events.Aggregate(currentState, _decider.Apply) : currentState;

        return this;
    }

    public Specification<TState> When<TResult>(Func<TState, Decision<TResult>> decisionFunc)
    {
        var currentState = _givenEvents.Aggregate(_initialState, _decider.Apply);

        var resultDecision = decisionFunc(currentState);
        SetDecision(resultDecision, resultDecision.IsSuccess ? resultDecision.Value : default);

        _finalState = resultDecision.IsSuccess
            ? resultDecision.Events.Aggregate(currentState, _decider.Apply)
            : currentState;

        return this;
    }

    public Specification<TState> ThenState(Action<TState> stateAssertion)
    {
        stateAssertion(_finalState);
        return this;
    }
}