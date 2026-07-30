using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class AuthorizePaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c001-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c001-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static AuthorizePaymentState Initiated() =>
        new AuthorizePaymentEvolver().Apply(
            new AuthorizePaymentState(),
            new PaymentInitiated(PaymentId, OrderId, 49.95m, "EUR", "card"));

    [Fact]
    public void Authorizes_an_initiated_payment()
    {
        var decision = AuthorizePaymentDecider.Decide(Initiated(), "AUTH-1", Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<PaymentAuthorized>()
            .Which.AuthorizationCode.Should().Be("AUTH-1");
    }

    [Fact]
    public void Refuses_an_unknown_payment()
    {
        var decision = AuthorizePaymentDecider.Decide(new AuthorizePaymentState(), "AUTH-1", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.not-found");
    }

    [Fact]
    public void Requires_an_authorization_code()
    {
        var decision = AuthorizePaymentDecider.Decide(Initiated(), "  ", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.authorization-code-required");
    }

    [Fact]
    public void Reports_Refunded_when_the_payment_has_already_been_refunded()
    {
        var evolver = new AuthorizePaymentEvolver();
        var state = evolver.Apply(Initiated(), new PaymentAuthorized(PaymentId, "AUTH-1", Now));
        state = evolver.Apply(state, new PaymentCaptured(PaymentId, 49.95m, Now));
        state = evolver.Apply(state, new PaymentRefunded(PaymentId, 49.95m, "", Now));

        var decision = AuthorizePaymentDecider.Decide(state, "AUTH-2", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Message.Should().Contain("Refunded");
    }
}
