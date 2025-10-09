using Alberto.EventSourcing;

namespace Alberto.UnitTests;

public class Specification : SpecificationBase
{
    public Specification When(Func<Decision> decisionFunc)
    {
        var decision = decisionFunc();
        SetDecision(decision);

        return this;
    }

    public Specification When<TResult>(Func<Decision<TResult>> decisionFunc)
    {
        var resultDecision = decisionFunc();
        SetDecision(resultDecision, resultDecision.IsSuccess ? resultDecision.Value : default);

        return this;
    }
}