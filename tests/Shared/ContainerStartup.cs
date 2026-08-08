using Docker.DotNet;
using DotNet.Testcontainers.Containers;

namespace Alberto.TestInfrastructure;

/// <summary>
/// Starts a Testcontainers container, retrying the one startup failure that is the host's
/// fault rather than the container's: the daemon picked a host port the kernel had already
/// handed out, and the bind lost the race.
/// </summary>
/// <remarks>
/// The real fix is a sysctl on the machine running the daemon, and it is not something a
/// repository can apply to whatever host clones it —
/// <c>docs/development/rootless-docker-ports.md</c> has the mechanism and the setting. This is
/// the backstop: it costs a few hundred milliseconds on the rare failing attempt, it needs no
/// host configuration, and it is what keeps CI honest on a host where nobody remembered.
/// <para>
/// This file is compiled into <c>Alberto.Tests</c>, <c>Alberto.Tests.Messaging.Rebus</c> and
/// <c>Alberto.Benchmarks</c> as a linked item — those are the three projects that start
/// containers, and none of them references the others. Every container in the repository must
/// start through here; a bare <see cref="IContainer.StartAsync"/> is the bug.
/// </para>
/// </remarks>
internal static class ContainerStartup
{
    /// <summary>
    /// A port collision is independent per attempt and rare, so a handful of tries takes the
    /// residual probability somewhere far below the rate at which anything else in the suite
    /// fails. More than that would be sitting on a genuinely wedged host instead of reporting it.
    /// </summary>
    private const int MaxAttempts = 4;

    /// <summary>
    /// Multiplied by the attempt number. The pause is not what makes the retry work — a new
    /// container draws a new port regardless — it only keeps a host that is momentarily out of
    /// ports from burning all four attempts inside a few milliseconds.
    /// </summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Builds and starts a container, discarding and rebuilding it if the host port bind lost
    /// the race described in the type remarks.
    /// </summary>
    /// <param name="build">
    /// Called once per attempt. It must hand back a <em>fresh</em> container: the host port is
    /// assigned when the daemon creates the container, so retrying the same instance would ask
    /// for the same allocation again rather than drawing a new one.
    /// </param>
    /// <param name="ct">Cancels the start and the delay between attempts.</param>
    /// <returns>The started container. The caller owns it and must dispose it.</returns>
    public static Task<TContainer> StartNewAsync<TContainer>(
        Func<TContainer> build,
        CancellationToken ct = default)
        where TContainer : IContainer =>
        StartNewCoreAsync(
            build,
            static (container, token) => container.StartAsync(token),
            static container => container.DisposeAsync().AsTask(),
            RetryDelay,
            ct);

    /// <summary>
    /// The retry policy itself, over delegates rather than <see cref="IContainer"/>, so that
    /// <c>ContainerStartupTests</c> can drive every branch — retry, give up, rethrow something
    /// unrelated — without a Docker daemon. That is the whole reason this indirection exists:
    /// a backstop nobody can exercise is a backstop nobody knows has stopped working.
    /// </summary>
    /// <param name="build">Produces a fresh subject for each attempt.</param>
    /// <param name="start">The operation that can lose the port race.</param>
    /// <param name="dispose">Discards a subject whose start failed.</param>
    /// <param name="retryDelay">Multiplied by the attempt number; zero in tests.</param>
    /// <param name="ct">Cancels the start and the delay between attempts.</param>
    internal static async Task<T> StartNewCoreAsync<T>(
        Func<T> build,
        Func<T, CancellationToken, Task> start,
        Func<T, Task> dispose,
        TimeSpan retryDelay,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var subject = build();
            try
            {
                await start(subject, ct);
                return subject;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsHostPortCollision(ex))
            {
                // The container was created before the bind failed, so it is a real container
                // holding a real name and has to be removed. Disposal failure is not worth
                // surfacing here: it would replace the diagnosis of a retryable port collision
                // with a cleanup error, and the reaper removes the leftover either way.
                try
                {
                    await dispose(subject);
                }
                catch
                {
                    // Ignored — see above.
                }

                await Task.Delay(retryDelay * attempt, ct);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="exception"/>, or anything it wraps, is the daemon reporting
    /// that it could not bind the host port it chose.
    /// </summary>
    /// <remarks>
    /// Two wordings, one condition. A rootless daemon fails inside RootlessKit's port manager
    /// and reports the kernel's <c>bind: address already in use</c>; a rootful one fails in its
    /// own allocator and reports <c>port is already allocated</c>. Matching on the message is
    /// unpleasant but unavoidable — <see cref="DockerApiException"/> carries no code that
    /// separates this from any other <c>InternalServerError</c>, and matching the type alone
    /// would retry genuine daemon errors that deserve to fail on the first attempt.
    /// </remarks>
    internal static bool IsHostPortCollision(Exception exception) =>
        Unwrap(exception).Any(e =>
            e is DockerApiException
            && (e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// <paramref name="exception"/> and every exception beneath it. Testcontainers surfaces the
    /// daemon's error directly today, but a wait strategy or a <see cref="Task.WhenAll(Task[])"/>
    /// upstream of it would nest the same failure, and the retry should still recognise it.
    /// </summary>
    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        yield return exception;

        IReadOnlyList<Exception> nested = exception switch
        {
            AggregateException aggregate => aggregate.InnerExceptions,
            { InnerException: { } inner } => [inner],
            _ => [],
        };

        foreach (var child in nested)
        {
            foreach (var descendant in Unwrap(child))
            {
                yield return descendant;
            }
        }
    }
}
