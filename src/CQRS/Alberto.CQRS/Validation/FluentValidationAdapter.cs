using Albert.EventSourcing;
using Alberto.CQRS.Results;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.CQRS.Validation;

/// <summary>
/// Adapter to integrate FluentValidation with the event sourcing validation pipeline.
/// </summary>
/// <typeparam name="T">The type to validate</typeparam>
public sealed class FluentValidationAdapter<T>(AbstractValidator<T> validator) : IValidator<T>
{
    public async Task<Result> ValidateAsync(T instance, CancellationToken cancellationToken = default)
    {
        ValidationResult validationResult = await validator.ValidateAsync(instance, cancellationToken);

        if (validationResult.IsValid)
            return Result.Success();

        var problems = validationResult.Errors
            .Select(e => Problem.Create(
                e.ErrorCode,
                e.ErrorMessage,
                new Dictionary<string, object>
                {
                    ["PropertyName"] = e.PropertyName, ["AttemptedValue"] = e.AttemptedValue ?? string.Empty
                }))
            .ToList();

        return Result.Fail(problems);
    }
}

/// <summary>
/// Extension methods for registering FluentValidation validators.
/// </summary>
public static class FluentValidationExtensions
{
    /// <summary>
    /// Registers a FluentValidation validator for a specific type.
    /// </summary>
    public static IServiceCollection AddFluentValidator<T, TValidator>(
        this IServiceCollection services,
        string? moduleKey = null)
        where TValidator : AbstractValidator<T>
    {
        if (moduleKey != null)
        {
            services.AddKeyedScoped<IValidator<T>>(moduleKey, (sp, _) =>
                new FluentValidationAdapter<T>(sp.GetRequiredKeyedService<TValidator>(moduleKey)));
            services.AddKeyedScoped<TValidator>(moduleKey);
        }
        else
        {
            services.AddScoped<IValidator<T>>(sp =>
                new FluentValidationAdapter<T>(sp.GetRequiredService<TValidator>()));
            services.AddScoped<TValidator>();
        }

        return services;
    }
}