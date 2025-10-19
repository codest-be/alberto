using Alberto.CQRS.Diagnostics;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.CQRS.Commands;

/// <summary>
/// Executes commands with optional validation.
/// </summary>
public sealed class CommandExecutor(IServiceProvider serviceProvider, string? moduleKey = null)
{
    private readonly IDiagnosticsEventListener? _diagnostics =
        serviceProvider.GetService<IDiagnosticsEventListener>();

    /// <summary>
    /// Executes a command without a return value.
    /// </summary>
    public async Task<Result> Execute<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand
    {
        using var scope = _diagnostics?.Command(typeof(TCommand), typeof(TCommand).Name, moduleKey, hasReturnValue: false)
                          ?? EmptyDisposable.Instance;

        // Validate if validator exists
        var validationResult = await ValidateCommand(command, cancellationToken);
        if (validationResult.IsFailure)
        {
            scope.WithValidationFailure(validationResult.Problems.Select(p => p.Code));
            return validationResult;
        }

        // Get and execute handler
        var handler = GetHandler<ICommandHandler<TCommand>>();
        if (handler == null)
        {
            var error = $"No handler found for command {typeof(TCommand).Name}";
            scope.WithError(error);
            return Result.Fail(error);
        }

        scope.WithHandler(handler.GetType());

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsSuccess)
            scope.WithOutcome("success");
        else
            scope.WithError("Command execution failed", result.Problems.Select(p => p.Code));

        return result;
    }

    /// <summary>
    /// Executes a command with a return value.
    /// </summary>
    public async Task<Result<TResult>> Execute<TCommand, TResult>(
        TCommand command,
        CancellationToken cancellationToken = default)
        where TCommand : ICommand
    {
        using var scope = _diagnostics?.Command(typeof(TCommand), typeof(TCommand).Name, moduleKey, hasReturnValue: true)
                          ?? EmptyDisposable.Instance;

        // Validate if validator exists
        var validationResult = await ValidateCommand(command, cancellationToken);
        if (validationResult.IsFailure)
        {
            scope.WithValidationFailure(validationResult.Problems.Select(p => p.Code));
            return Result<TResult>.Fail(validationResult.Problems);
        }

        // Get and execute handler
        var handler = GetHandler<ICommandHandler<TCommand, TResult>>();
        if (handler == null)
        {
            var error = $"No handler found for command {typeof(TCommand).Name}";
            scope.WithError(error);
            return Result<TResult>.Fail(error);
        }

        scope.WithHandler(handler.GetType());

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsSuccess)
            scope.WithOutcome("success");
        else
            scope.WithError("Command execution failed", result.Problems.Select(p => p.Code));

        return result;
    }

    private async Task<Result> ValidateCommand<TCommand>(TCommand command, CancellationToken cancellationToken)
        where TCommand : ICommand
    {
        var validator = GetService<IValidator<TCommand>>();
        if (validator == null)
            return Result.Success();

        var validationResult = await validator.ValidateAsync(command, cancellationToken);

        if (validationResult.IsValid)
            return Result.Success();

        var problems = validationResult.Errors
            .Select(error => Problem.Create(
                code: IsFluentValidationDefaultCode(error.ErrorCode) ? "VALIDATION_ERROR" : error.ErrorCode,
                message: error.ErrorMessage))
            .ToList();

        return Result.Fail(problems);
    }

    private static bool IsFluentValidationDefaultCode(string errorCode)
    {
        // FluentValidation default error codes end with "Validator" (e.g., "NotEmptyValidator", "GreaterThanValidator")
        return errorCode.EndsWith("Validator", StringComparison.Ordinal);
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