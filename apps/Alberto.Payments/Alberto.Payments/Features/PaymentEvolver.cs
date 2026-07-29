using Alberto.Dcb;
using Alberto.Payments.Contracts;
using PaymentActions = Alberto.Payments.Features.PaymentDecider;

namespace Alberto.Payments.Features;

/// <summary>
/// Reconstitutes PaymentState from events. Used on the command side to load current state.
/// </summary>
public sealed class PaymentEvolver : Evolver<PaymentState>,
    IEvolve<PaymentState, PaymentInitiated>,
    IEvolve<PaymentState, PaymentAuthorized>,
    IEvolve<PaymentState, PaymentCaptured>,
    IEvolve<PaymentState, PaymentFailed>,
    IEvolve<PaymentState, PaymentRefunded>
{
    private readonly PaymentActions _decider = new();

    public PaymentState Apply(PaymentState state, PaymentInitiated e) => _decider.Apply(state, e);
    public PaymentState Apply(PaymentState state, PaymentAuthorized e) => _decider.Apply(state, e);
    public PaymentState Apply(PaymentState state, PaymentCaptured e) => _decider.Apply(state, e);
    public PaymentState Apply(PaymentState state, PaymentFailed e) => _decider.Apply(state, e);
    public PaymentState Apply(PaymentState state, PaymentRefunded e) => _decider.Apply(state, e);
}
