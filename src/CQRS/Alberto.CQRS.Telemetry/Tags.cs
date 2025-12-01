namespace Alberto.CQRS.Telemetry;

internal static class Tags
{
    // Command tags
    public const string CommandType = "command.type";
    public const string CommandName = "command.name";
    public const string CommandHandlerType = "command.handler_type";
    public const string CommandHasReturnValue = "command.has_return_value";

    // Query tags
    public const string QueryType = "query.type";
    public const string QueryName = "query.name";
    public const string QueryHandlerType = "query.handler_type";
    public const string QueryResultType = "query.result_type";

    // Common tags
    public const string ModuleKey = "module.key";
    public const string Outcome = "outcome";
    public const string ValidatorType = "validation.handler_type";
    public const string ValidatorName = "validation.handler_name";
    public const string ValidationFailed = "validation.failed";
    public const string ProblemCodes = "problem.codes";
    public const string ErrorMessage = "error.message";
}