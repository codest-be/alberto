using Alberto.CQRS.Diagnostics;
using Alberto.CQRS.Registration;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.CQRS.Telemetry;

/// <summary>
/// Extension methods for adding OpenTelemetry support to CQRS builders.
/// </summary>
public static class CQRSBuilderExtensions
{
    /// <summary>
    /// Enables OpenTelemetry Activity-based tracing for commands and queries.
    /// Registers an ActivityDiagnosticEventListener that creates Activities for each
    /// command and query execution.
    /// </summary>
    /// <param name="builder">The CQRS builder</param>
    /// <returns>The CQRS builder for chaining</returns>
    public static CQRSBuilder WithTelemetry(this CQRSBuilder builder)
    {
        builder.Services.AddSingleton<IDiagnosticsEventListener, ActivityDiagnosticEventListener>();
        return builder;
    }
}