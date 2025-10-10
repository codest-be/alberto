using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alberto.ComponentTests.Steps;
using Microsoft.Extensions.Logging;

namespace Alberto.ComponentTests;

public sealed class UseCase(ScenarioContext context)
{
    private static readonly JsonSerializerOptions StepJsonSerializerOptions = new()
    {
        IncludeFields = true, // To show tuple fields
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private List<IStep> _actSteps = [];

    private List<IStep> _arrangeSteps = [];
    private List<IStep> _assertSteps = [];

    private static ConcurrentDictionary<Type, string> TypeNameCache { get; } = new();

    public UseCase Background(params IStep[] backgroundSteps)
    {
        if (backgroundSteps.Length == 0)
            throw new ArgumentException("At least one background step must be provided", nameof(backgroundSteps));

        context.BackgroundSteps.AddRange(backgroundSteps);
        return this;
    }

    public UseCase Arrange(params IStep[] arrangeSteps)
    {
        if (arrangeSteps.Length == 0)
            throw new ArgumentException("At least one arrange step must be provided", nameof(arrangeSteps));

        _arrangeSteps.AddRange(arrangeSteps);
        return this;
    }

    public UseCase Act(params IStep[] actSteps)
    {
        if (actSteps.Length == 0)
            throw new ArgumentException("At least one act step must be provided", nameof(actSteps));

        _actSteps.AddRange(actSteps);
        return this;
    }

    public Task Assert(params IStep[] assertSteps)
    {
        if (assertSteps.Length == 0)
            throw new ArgumentException("At least one assert step must be provided", nameof(assertSteps));

        _assertSteps.AddRange(assertSteps);
        return Execute();
    }

    private async Task Execute()
    {
        var logger = context.CreateLogger();

        if (context.BackgroundSteps.Any())
        {
            logger.LogInformation("😶‍🌫️ Background");
            foreach (var step in context.BackgroundSteps)
                await ExecuteStep(step, context, logger);
        }

        if (_arrangeSteps.Any())
        {
            logger.LogInformation("🔧 Arrange");
            foreach (var step in _arrangeSteps)
                await ExecuteStep(step, context, logger);
        }

        if (_actSteps.Any())
        {
            logger.LogInformation("🚀 Act");
            foreach (var step in _actSteps)
                await ExecuteStep(step, context, logger);
        }

        if (_assertSteps.Any())
        {
            logger.LogInformation("🔎 Assert");
            foreach (var step in _assertSteps)
                await ExecuteStep(step, context, logger);
        }
    }

    private static async Task ExecuteStep(IStep step, ScenarioContext context, ILogger logger)
    {
        var stepName = GetTypeName(step);
        var stepInfo = GetPropertyInfo(step);

        var stopwatch = context.LogStepDuration ? Stopwatch.StartNew() : null;

        if (stepInfo is not null)
            logger.LogInformation("   🐾 {stepName} > {stepInfo}", stepName, stepInfo);
        else
            logger.LogInformation("   🐾 {stepName}", stepName);

        using var serviceScope = context.CreateAndUseNewServiceScope();

        try
        {
            await step.Execute(context);
        }
        catch
        {
            logger.LogError("   💥 {stepName} failed", stepName);
            throw;
        }
        finally
        {
            if (stopwatch is not null)
            {
                stopwatch.Stop();
                logger.LogInformation("   ⏱️ {stepName} completed in {elapsedMilliseconds} ms", stepName,
                    stopwatch.ElapsedMilliseconds);
            }
        }
    }

    private static string GetTypeName(IStep step)
    {
        return TypeNameCache.GetOrAdd(step.GetType(), type =>
        {
            if (!type.IsGenericType)
                return type.Name;

            var baseTypeName = type.Name[..type.Name.IndexOf('`')];
            return $"{baseTypeName}<{string.Join(", ", type.GenericTypeArguments.Select(t => t.Name))}>";
        });
    }

    private static string? GetPropertyInfo(IStep step)
    {
        var text = JsonSerializer.Serialize(step, step.GetType(), StepJsonSerializerOptions);
        return text != "{}" ? text : null;
    }
}