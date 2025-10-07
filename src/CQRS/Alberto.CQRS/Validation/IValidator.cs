using Alberto.CQRS.Results;

namespace Alberto.CQRS.Validation;

/// <summary>
/// Interface for command and query validators.
/// Implementations can use any validation library (e.g., FluentValidation).
/// </summary>
/// <typeparam name="T">The type to validate</typeparam>
public interface IValidator<in T>
{
    /// <summary>
    /// Validates the given instance.
    /// </summary>
    /// <param name="instance">The instance to validate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A result indicating validation success or containing validation problems</returns>
    Task<Result> ValidateAsync(T instance, CancellationToken cancellationToken = default);
}