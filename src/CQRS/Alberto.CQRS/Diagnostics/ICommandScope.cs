namespace Alberto.CQRS.Diagnostics;

/// <summary>
/// Represents a scope for command execution telemetry.
/// Provides methods to enrich telemetry with execution details.
/// </summary>
public interface ICommandScope : IDisposable
{
    /// <summary>
    /// Records the handler type that processed this command.
    /// </summary>
    ICommandScope WithHandler(Type handlerType);

    /// <summary>
    /// Records a successful outcome.
    /// </summary>
    ICommandScope WithOutcome(string outcome);

    /// <summary>
    /// Records the handler type that validated this command.
    /// </summary>
    IValidatorScope WithValidator(Type handlerType);

    /// <summary>
    /// Records an error with optional problem codes.
    /// </summary>
    ICommandScope WithError(string errorMessage, IEnumerable<string>? problemCodes = null);
}

public interface IValidatorScope : IDisposable
{
    /// <summary>
    /// Records a validation failure with problem codes.
    /// </summary>
    IValidatorScope WithValidationFailure(IEnumerable<string> problemCodes);
}