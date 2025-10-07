using Alberto.CQRS.Results;

namespace Alberto.CQRS.Commands;

/// <summary>
/// Handler for commands that do not return a result value.
/// </summary>
/// <typeparam name="TCommand">The command type to handle</typeparam>
public interface ICommandHandler<in TCommand> where TCommand : ICommand
{
    /// <summary>
    /// Handles the command execution.
    /// </summary>
    /// <param name="command">The command to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A result indicating success or failure</returns>
    Task<Result> Handle(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Handler for commands that return a result value.
/// </summary>
/// <typeparam name="TCommand">The command type to handle</typeparam>
/// <typeparam name="TResult">The result type</typeparam>
public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand
{
    /// <summary>
    /// Handles the command execution and returns a result.
    /// </summary>
    /// <param name="command">The command to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A result containing the value or errors</returns>
    Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken = default);
}