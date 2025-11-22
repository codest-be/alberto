using System.Diagnostics;
using Alberto.CQRS.Diagnostics;

namespace Alberto.CQRS.Telemetry.Scopes;

internal sealed class CommandScope(Activity activity) : ICommandScope
{
    public const string ActivityName = "Alberto.CQRS.Command";
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;

        activity.Dispose();
        _disposed = true;
    }

    public ICommandScope WithHandler(Type handlerType)
    {
        activity.SetTag(Tags.CommandHandlerType, handlerType.FullName ?? handlerType.Name);
        return this;
    }

    public ICommandScope WithOutcome(string outcome)
    {
        activity.SetTag(Tags.Outcome, outcome);
        return this;
    }

    public ICommandScope WithValidationFailure(IEnumerable<string> problemCodes)
    {
        activity.SetTag(Tags.ValidationFailed, true);
        activity.SetTag(Tags.Outcome, "validation_failure");
        activity.SetTag(Tags.ProblemCodes, string.Join(", ", problemCodes));
        return this;
    }

    public ICommandScope WithError(string errorMessage, IEnumerable<string>? problemCodes = null)
    {
        activity.SetTag(Tags.Outcome, "failure");
        activity.SetTag(Tags.ErrorMessage, errorMessage);

        if (problemCodes != null)
            activity.SetTag(Tags.ProblemCodes, string.Join(", ", problemCodes));

        activity.SetStatus(ActivityStatusCode.Error);

        return this;
    }

    public CommandScope WithCommandInfo(Type commandType, string commandName, string? moduleKey, bool hasReturnValue)
    {
        activity.DisplayName = $"Command: {commandName}";
        activity.SetTag(Tags.CommandType, commandType.FullName ?? commandType.Name);
        activity.SetTag(Tags.CommandName, commandName);
        activity.SetTag(Tags.CommandHasReturnValue, hasReturnValue);

        if (!string.IsNullOrEmpty(moduleKey))
            activity.SetTag(Tags.ModuleKey, moduleKey);

        return this;
    }
}