using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class AuthorizePaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c001-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c001-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static readonly PaymentInitiated Initiated =
        new(PaymentId, OrderId, 49.95m, "EUR", "card");

    [Fact]
    public void Authorizes_an_initiated_payment()
    {
        Spec.For(new AuthorizePaymentEvolver())
            .Given(Initiated)
            .When(state => AuthorizePaymentDecider.Decide(state, "AUTH-1", Now))
            .ThenEmitsOnly<PaymentAuthorized>(e => e.AuthorizationCode == "AUTH-1")
            .ThenState(s => s.Status.Should().Be(PaymentStatus.Authorized));
    }

    [Fact]
    public void Refuses_an_unknown_payment()
    {
        Spec.For(new AuthorizePaymentEvolver())
            .GivenNoEvents()
            .When(state => AuthorizePaymentDecider.Decide(state, "AUTH-1", Now))
            .ThenFails(PaymentProblems.NotFound());
    }

    [Fact]
    public void Requires_an_authorization_code()
    {
        Spec.For(new AuthorizePaymentEvolver())
            .Given(Initiated)
            .When(state => AuthorizePaymentDecider.Decide(state, "  ", Now))
            .ThenFails(PaymentProblems.AuthorizationCodeRequired());
    }

    [Fact]
    public void Reports_Refunded_when_the_payment_has_already_been_refunded()
    {
        Spec.For(new AuthorizePaymentEvolver())
            .Given(
                Initiated,
                new PaymentAuthorized(PaymentId, "AUTH-1", Now),
                new PaymentCaptured(PaymentId, 49.95m, Now),
                new PaymentRefunded(PaymentId, 49.95m, "", Now))
            .When(state => AuthorizePaymentDecider.Decide(state, "AUTH-2", Now))
            .ThenFails("payment.invalid-status")
            .ThenProblems(problems => problems.Single().Message.Should().Contain("Refunded"));
    }
}
