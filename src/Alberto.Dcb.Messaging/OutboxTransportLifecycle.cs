using System.Runtime.ExceptionServices;

namespace Alberto.Dcb.Messaging;

/// <summary>
/// Owns one transport from its first relay start until its last relay stops. The module is
/// intentionally internal: callers supply a transport, while relays exercise its lifecycle
/// through per-relay leases.
/// </summary>
internal sealed class OutboxTransportLifecycle
{
    internal const string CleanupFailureDataKey =
        "Alberto.Dcb.Messaging.TransportStopException";

    internal static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(30);

    private readonly IMessageTransport _transport;
    private readonly TimeSpan _stopTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private LifecycleState _state;
    private int _activeRelays;
    private ExceptionDispatchInfo? _startupFailure;

    internal OutboxTransportLifecycle(
        IMessageTransport transport,
        TimeSpan? stopTimeout = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var resolvedStopTimeout = stopTimeout ?? DefaultStopTimeout;
        if (resolvedStopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopTimeout),
                "The transport stop timeout must be positive.");
        }

        _transport = transport;
        _stopTimeout = resolvedStopTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal OutboxTransportLease CreateLease() => new(this);

    internal async Task StartAsync(CancellationToken ct)
    {
        await _transition.WaitAsync(CancellationToken.None);
        try
        {
            if (_state == LifecycleState.Running)
            {
                _activeRelays++;
                return;
            }

            if (_startupFailure is not null)
                _startupFailure.Throw();

            if (_state != LifecycleState.Created)
            {
                throw new InvalidOperationException(
                    "The outbox transport lifecycle has already stopped and cannot be restarted.");
            }

            _state = LifecycleState.Starting;
            try
            {
                await _transport.StartAsync(ct);
                _activeRelays = 1;
                _state = LifecycleState.Running;
            }
            catch (Exception startupFailure)
            {
                _startupFailure = ExceptionDispatchInfo.Capture(startupFailure);
                _state = LifecycleState.Stopping;
                try
                {
                    // StartAsync may have initialized resources before it failed. Once Alberto
                    // invokes StartAsync, ownership includes one bounded cleanup attempt.
                    await StopTransportAsync();
                }
                catch (Exception cleanupFailure)
                {
                    AttachCleanupFailure(startupFailure, cleanupFailure);
                }
                finally
                {
                    _state = LifecycleState.Stopped;
                }

                throw;
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    internal Task PublishAsync(ExternalMessage message, CancellationToken ct) =>
        _transport.PublishAsync(message, ct);

    internal async Task StopAsync(Exception? executionFailure)
    {
        await _transition.WaitAsync(CancellationToken.None);
        try
        {
            if (_state != LifecycleState.Running)
                return;

            _activeRelays--;
            if (_activeRelays > 0)
                return;

            _state = LifecycleState.Stopping;
            try
            {
                await StopTransportAsync();
            }
            catch (Exception cleanupFailure)
            {
                if (executionFailure is null)
                    throw;

                AttachCleanupFailure(executionFailure, cleanupFailure);
            }
            finally
            {
                _state = LifecycleState.Stopped;
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task StopTransportAsync()
    {
        using var timeout = new CancellationTokenSource(_stopTimeout, _timeProvider);
        // Invoke StopAsync on the thread pool so the lifecycle timeout also bounds transports
        // that block synchronously before returning their Task.
        var cleanupTask = Task.Run(
            () => _transport.StopAsync(timeout.Token),
            CancellationToken.None);

        try
        {
            await cleanupTask.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            ObserveLateFailure(cleanupTask);
            throw new TimeoutException(
                $"The outbox transport did not stop within {_stopTimeout}.",
                exception);
        }
    }

    private static void AttachCleanupFailure(Exception primaryFailure, Exception cleanupFailure)
    {
        try
        {
            primaryFailure.Data[CleanupFailureDataKey] = cleanupFailure;
        }
        catch
        {
            // Some custom exceptions expose a read-only Data dictionary. The causal failure still
            // has precedence even when the secondary cleanup failure cannot be attached.
        }
    }

    private static void ObserveLateFailure(Task cleanupTask)
    {
        if (cleanupTask.IsCompleted)
            return;

        _ = cleanupTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private enum LifecycleState
    {
        Created,
        Starting,
        Running,
        Stopping,
        Stopped,
    }
}

/// <summary>
/// Gives one relay access to a shared transport lifecycle and makes release idempotent.
/// </summary>
internal sealed class OutboxTransportLease
{
    private readonly OutboxTransportLifecycle _owner;
    private readonly object _stopGate = new();
    private Task? _stopTask;
    private int _state;

    internal OutboxTransportLease(OutboxTransportLifecycle owner) => _owner = owner;

    internal async Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("This outbox transport lease has already started.");

        try
        {
            await _owner.StartAsync(ct);
            Volatile.Write(ref _state, 2);
        }
        catch
        {
            Volatile.Write(ref _state, 4);
            throw;
        }
    }

    internal Task PublishAsync(ExternalMessage message, CancellationToken ct)
    {
        if (Volatile.Read(ref _state) != 2)
        {
            throw new InvalidOperationException(
                "The outbox transport is not available for publishing.");
        }

        return _owner.PublishAsync(message, ct);
    }

    internal Task StopAsync(Exception? executionFailure)
    {
        lock (_stopGate)
        {
            if (_stopTask is not null)
                return _stopTask;

            if (Volatile.Read(ref _state) != 2)
                return Task.CompletedTask;

            Volatile.Write(ref _state, 3);
            return _stopTask = StopCoreAsync(executionFailure);
        }
    }

    private async Task StopCoreAsync(Exception? executionFailure)
    {
        try
        {
            await _owner.StopAsync(executionFailure);
        }
        finally
        {
            Volatile.Write(ref _state, 4);
        }
    }
}

/// <summary>
/// Shares lifecycle owners by transport reference within one dependency-injection provider.
/// </summary>
internal sealed class OutboxTransportLifecycleRegistry
{
    private readonly Dictionary<IMessageTransport, OutboxTransportLifecycle> _lifecycles =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    internal OutboxTransportLease CreateLease(IMessageTransport transport)
    {
        lock (_gate)
        {
            if (!_lifecycles.TryGetValue(transport, out var lifecycle))
            {
                lifecycle = new OutboxTransportLifecycle(transport);
                _lifecycles.Add(transport, lifecycle);
            }

            return lifecycle.CreateLease();
        }
    }
}
