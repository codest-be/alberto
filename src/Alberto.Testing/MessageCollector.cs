using System.Diagnostics.CodeAnalysis;
using Alberto.Messaging;

namespace Alberto.Testing;

/// <summary>
/// A transport that records every message the outbox relay publishes, so a test can wait for one.
/// </summary>
/// <remarks>
/// <para>
/// A message leaves through the relay some time after the command that caused it, so a test that
/// asserts on it in the next line is asserting on a race. Pass the collector as the outbox's transport
/// (<c>WithOutbox(..., transport: collector)</c>) and await <see cref="WaitForPublishedAsync(Func{ExternalMessage, bool}, string, TimeSpan?, CancellationToken)"/>.
/// </para>
/// <para>
/// Give it an <c>inner</c> transport to keep delivering to the real consumer: the collector records a
/// message only once the inner transport accepted it, so it records what was delivered, and a
/// redelivery after a failure is recorded again.
/// </para>
/// <para>This class is thread-safe.</para>
/// </remarks>
public sealed class MessageCollector : IMessageTransport
{
    private readonly List<ExternalMessage> _published = [];
    private readonly IMessageTransport? _inner;
    private readonly TimeProvider? _timeProvider;

    /// <summary>Creates a collector.</summary>
    /// <param name="inner">The transport to deliver to before recording; null records only.</param>
    /// <param name="timeProvider">Clock for the wait deadline; defaults to <see cref="TimeProvider.System"/>.</param>
    public MessageCollector(IMessageTransport? inner = null, TimeProvider? timeProvider = null)
    {
        _inner = inner;
        _timeProvider = timeProvider;
    }

    /// <summary>Every message published so far, in the order it was published.</summary>
    public IReadOnlyList<ExternalMessage> Published
    {
        get { lock (_published) return _published.ToList(); }
    }

    /// <summary>
    /// Waits until a published message matches <paramref name="predicate"/> and returns it,
    /// or throws <see cref="TimeoutException"/> naming <paramref name="what"/>.
    /// </summary>
    /// <param name="predicate">Called for each published message, including those published before the wait began.</param>
    /// <param name="what">Completes "timed out waiting for ...": make it specific.</param>
    /// <param name="timeout">Defaults to <see cref="Poll.DefaultTimeout"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by their leading parameters - a predicate against " +
                        "a message type and tenant - not by the optional tail.")]
    public async Task<ExternalMessage> WaitForPublishedAsync(
        Func<ExternalMessage, bool> predicate,
        string what,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ExternalMessage? match = null;
        await Poll.UntilAsync(() =>
        {
            lock (_published) match = _published.FirstOrDefault(predicate);
            return match is not null;
        }, what, timeout, timeProvider: _timeProvider, cancellationToken: cancellationToken).ConfigureAwait(false);
        return match!;
    }

    /// <summary>
    /// Waits until a message of <paramref name="messageType"/> is published, for <paramref name="tenantId"/> when given.
    /// Scope by tenant when tests share a host: another test's message of the same type would match otherwise.
    /// </summary>
    /// <inheritdoc cref="WaitForPublishedAsync(Func{ExternalMessage, bool}, string, TimeSpan?, CancellationToken)"/>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by their leading parameters - a predicate against " +
                        "a message type and tenant - not by the optional tail.")]
    public Task<ExternalMessage> WaitForPublishedAsync(
        string messageType,
        string? tenantId = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        return WaitForPublishedAsync(
            m => m.MessageType == messageType && (tenantId is null || m.TenantId == tenantId),
            tenantId is null ? $"a {messageType} message" : $"a {messageType} message for tenant {tenantId}",
            timeout, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task PublishAsync(ExternalMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_inner is not null) await _inner.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        lock (_published) _published.Add(message);
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
        => _inner?.StartAsync(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken = default)
        => _inner?.StopAsync(cancellationToken) ?? Task.CompletedTask;
}
