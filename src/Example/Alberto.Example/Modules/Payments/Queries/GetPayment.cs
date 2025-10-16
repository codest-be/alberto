using Alberto.CQRS.Queries;
using Alberto.CQRS.Results;
using Alberto.Example.Modules.Payments.Api.Contracts;
using Alberto.Example.Modules.Payments.Projections;
using Alberto.Projections;

namespace Alberto.Example.Modules.Payments.Queries;

public sealed record GetPaymentQuery(Guid PaymentId) : IQuery;

public sealed class GetPaymentHandler(IProjectionRepository<Guid, Payment> repository)
    : IQueryHandler<GetPaymentQuery, PaymentDto>
{
    public async Task<Result<PaymentDto>> Handle(GetPaymentQuery query, CancellationToken cancellationToken = default)
    {
        var payment = await repository.Get(query.PaymentId, cancellationToken);

        if (payment == null)
            return Result<PaymentDto>.Fail(PaymentProblems.PaymentNotFound(query.PaymentId));

        var dto = new PaymentDto(
            payment.PaymentId,
            payment.OrderId,
            payment.Amount,
            payment.Status.ToString("G"));

        return Result<PaymentDto>.Success(dto);
    }
}