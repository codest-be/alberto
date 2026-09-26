using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace Alberto.Postgres;

/// <summary>
/// Forwards DbUp's migration output to the host's <see cref="ILogger"/>, so migration logs land
/// in the application's logging pipeline instead of on the console.
/// </summary>
internal sealed class UpgradeLogAdapter(ILogger logger) : IUpgradeLog
{
#pragma warning disable CA2254 // DbUp supplies the template; it is not a compile-time constant.
    public void LogTrace(string format, params object[] args) => logger.LogTrace(format, args);

    public void LogDebug(string format, params object[] args) => logger.LogDebug(format, args);

    public void LogInformation(string format, params object[] args) => logger.LogInformation(format, args);

    public void LogWarning(string format, params object[] args) => logger.LogWarning(format, args);

    public void LogError(string format, params object[] args) => logger.LogError(format, args);

    public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, format, args);
#pragma warning restore CA2254
}
