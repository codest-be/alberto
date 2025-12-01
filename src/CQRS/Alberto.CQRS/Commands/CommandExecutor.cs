using Alberto.CQRS.Diagnostics;
using Alberto.CQRS.Results;
using Alberto.CQRS.Validators;
using Alberto.EventSourcing;
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
        where TCommand : ICommand<Unit>
    {
        using var scope = _diagnostics?.Command(typeof(TCommand), typeof(TCommand).Name, moduleKey, hasReturnValue: false)
                          ?? EmptyDisposable.Instance;

        // Validate if validator exists
        var validators = GetHandlers<IValidator<TCommand>>().ToArray();
        var allProblems = new List<Problem>();

        foreach (var validator in validators)
        {
            using var validationScope = scope?.WithValidator(validator.GetType()) ?? EmptyDisposable.Instance;

            await foreach (var problem in validator.Validate(command, cancellationToken))
            {
                allProblems.Add(problem);
            }

            if (allProblems.Any())
            {
                validationScope.WithValidationFailure(allProblems.Select(p => p.Code));
            }
        }

        if (allProblems.Any())
        {
            return Result.Fail(allProblems);
        }

        // Get and execute handler
        var handler = GetHandler<ICommandHandler<TCommand, Unit>>();
        if (handler == null)
        {
            var error = $"No handler found for command {typeof(TCommand).Name}";
            scope?.WithError(error);
            return Result.Fail(error);
        }

        scope?.WithHandler(handler.GetType());

        var result = await handler.Handle(command, cancellationToken);

        if (result.IsSuccess)
            scope?.WithOutcome("success");
        else
            scope?.WithError("Command execution failed", result.Problems.Select(p => p.Code));

        return Result.Success();
    }

    /// <summary>
    /// Executes a command with a return value.
    /// </summary>
    public async Task<Result<TResult>> Execute<TCommand, TResult>(
        TCommand command,
        CancellationToken cancellationToken = default)
        where TCommand : ICommand<TResult>
    {
        using var scope = _diagnostics?.Command(typeof(TCommand), typeof(TCommand).Name, moduleKey, hasReturnValue: true)
                          ?? EmptyDisposable.Instance;

        // Validate if validator exists
        var validators = GetHandlers<IValidator<TCommand>>().ToArray();
        var allProblems = new List<Problem>();

        foreach (var validator in validators)
        {
            using var validatorScope = scope.WithValidator(validator.GetType());

            await foreach (var problem in validator.Validate(command, cancellationToken))
            {
                allProblems.Add(problem);
            }

            if (allProblems.Any())
            {
                validatorScope.WithValidationFailure(allProblems.Select(p => p.Code));
            }
        }

        if (allProblems.Any())
        {
            return Result<TResult>.Fail(allProblems);
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

    private TService? GetHandler<TService>() where TService : class
    {
        return moduleKey != null
            ? serviceProvider.GetKeyedService<TService>(moduleKey)
            : serviceProvider.GetService<TService>();
    }

    private IEnumerable<TService> GetHandlers<TService>() where TService : class
    {
        return moduleKey != null
            ? serviceProvider.GetKeyedServices<TService>(moduleKey)
            : serviceProvider.GetServices<TService>();
    }

    private TService? GetService<TService>() where TService : class
    {
        return moduleKey != null
            ? serviceProvider.GetKeyedService<TService>(moduleKey)
            : serviceProvider.GetService<TService>();
    }
}