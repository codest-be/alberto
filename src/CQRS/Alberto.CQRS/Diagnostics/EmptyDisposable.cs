namespace Alberto.CQRS.Diagnostics;

/// <summary>
/// A singleton empty scope that does nothing when disposed or when methods are called.
/// </summary>
internal sealed class EmptyDisposable : ICommandScope, IQueryScope
{
    public static readonly EmptyDisposable Instance = new();

    private EmptyDisposable()
    {
    }

    public void Dispose()
    {
    }

    public ICommandScope WithHandler(Type handlerType) => this;
    public ICommandScope WithOutcome(string outcome) => this;
    public ICommandScope WithValidationFailure(IEnumerable<string> problemCodes) => this;
    public ICommandScope WithError(string errorMessage, IEnumerable<string>? problemCodes = null) => this;

    IQueryScope IQueryScope.WithHandler(Type handlerType) => this;
    IQueryScope IQueryScope.WithOutcome(string outcome) => this;
    IQueryScope IQueryScope.WithError(string errorMessage, IEnumerable<string>? problemCodes) => this;
}