using Alberto.CQRS.Diagnostics;

namespace Alberto.CQRS.Telemetry.Scopes;

/// <summary>
/// Represents an empty scope for diagnostic events when telemetry is not enabled.
/// </summary>
internal sealed class EmptyScope : ICommandScope, IQueryScope, IValidatorScope
{
    public static readonly EmptyScope Instance = new();

    private EmptyScope()
    {
    }

    public void Dispose()
    {
    }

    public ICommandScope WithHandler(Type handlerType) => this;
    public ICommandScope WithOutcome(string outcome) => this;
    public IValidatorScope WithValidator(Type handlerType) => this;
    public ICommandScope WithError(string errorMessage, IEnumerable<string>? problemCodes = null) => this;

    IQueryScope IQueryScope.WithHandler(Type handlerType) => this;
    IQueryScope IQueryScope.WithOutcome(string outcome) => this;
    IQueryScope IQueryScope.WithError(string errorMessage, IEnumerable<string>? problemCodes) => this;

    IValidatorScope IValidatorScope.WithValidationFailure(IEnumerable<string> problemCodes) => this;

    public ICommandScope WithValidationFailure(IEnumerable<string> problemCodes) => this;
}