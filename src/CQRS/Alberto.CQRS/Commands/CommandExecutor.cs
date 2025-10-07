using Alberto.CQRS.Results;
using Alberto.CQRS.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.CQRS.Commands;

/// <summary>
/// Executes commands with optional validation.
/// </summary>
public sealed class CommandExecutor(IServiceProvider serviceProvider, string? moduleKey = null)
{
    /// <summary>
    /// Executes a command without a return value.
    /// </summary>
    public async Task<Result> Execute<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand
    {
        // Validate if validator exists
        var validationResult = await ValidateCommand(command, cancellationToken);
        if (validationResult.IsFailure)
            return validationResult;

        // Get and execute handler
        var handler = GetHandler<ICommandHandler<TCommand>>();
        if (handler == null)
            return Result.Fail($"No handler found for command {typeof(TCommand).Name}");

        return await handler.Handle(command, cancellationToken);
    }

    /// <summary>
    /// Executes a command with a return value.
    /// </summary>
    public async Task<Result<TResult>> Execute<TCommand, TResult>(
        TCommand command,
        CancellationToken cancellationToken = default)
        where TCommand : ICommand
    {
        // Validate if validator exists
        var validationResult = await ValidateCommand(command, cancellationToken);
        if (validationResult.IsFailure)
            return Result<TResult>.Fail(validationResult.Problems);

        // Get and execute handler
        var handler = GetHandler<ICommandHandler<TCommand, TResult>>();
        if (handler == null)
            return Result<TResult>.Fail($"No handler found for command {typeof(TCommand).Name}");

        return await handler.Handle(command, cancellationToken);
    }

    private Task<Result> ValidateCommand<TCommand>(TCommand command, CancellationToken cancellationToken)
        where TCommand : ICommand
    {
        var validator = GetService<IValidator<TCommand>>();
        if (validator == null)
            return Task.FromResult(Result.Success());

        return Task.FromResult(validator.Validate(command));
    }

    private TService? GetHandler<TService>() where TService : class
    {
        return moduleKey != null
            ? serviceProvider.GetKeyedService<TService>(moduleKey)
            : serviceProvider.GetService<TService>();
    }

    private TService? GetService<TService>() where TService : class
    {
        return moduleKey != null
            ? serviceProvider.GetKeyedService<TService>(moduleKey)
            : serviceProvider.GetService<TService>();
    }
}