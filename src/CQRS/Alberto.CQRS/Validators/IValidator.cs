using Alberto.EventSourcing;

namespace Alberto.CQRS.Validators;

public interface IValidator<in T>
{
    IAsyncEnumerable<Problem> Validate(T instance, CancellationToken cancellationToken);
}