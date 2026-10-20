using FhirAugury.DevUi.Configuration;
using FhirAugury.Processing.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.DevUi.Services;

public sealed class RunPollingSession : IAsyncDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly ILogger<RunPollingSession> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _sync = new();

    private Func<CancellationToken, Task<AuthoringRunResponse>>? _refresh;
    private CancellationTokenSource? _lifetimeCancellation;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private bool _started;
    private bool _terminal;
    private bool _disposed;

    public RunPollingSession(
        TimeProvider timeProvider,
        IOptions<DevUiOptions> options,
        ILogger<RunPollingSession> logger)
        : this(
            timeProvider,
            options.Value.RunPollInterval,
            logger)
    {
    }

    public RunPollingSession(
        TimeProvider timeProvider,
        TimeSpan interval)
        : this(
            timeProvider,
            interval,
            NullLogger<RunPollingSession>.Instance)
    {
    }

    private RunPollingSession(
        TimeProvider timeProvider,
        TimeSpan interval,
        ILogger<RunPollingSession> logger)
    {
        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                interval,
                "Polling interval must be greater than zero.");
        }
        _interval = interval;
        _logger = logger ??
            throw new ArgumentNullException(nameof(logger));
    }

    public bool IsPolling
    {
        get
        {
            lock (_sync)
            {
                return _started &&
                    !_terminal &&
                    !_disposed &&
                    _lifetimeCancellation
                        ?.IsCancellationRequested != true;
            }
        }
    }

    public async Task<AuthoringRunResponse> StartAsync(
        Func<CancellationToken, Task<AuthoringRunResponse>> refresh,
        CancellationToken navigationCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_started)
            {
                throw new InvalidOperationException(
                    "This polling session has already been started.");
            }

            _started = true;
            _refresh = refresh;
            _lifetimeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    navigationCancellation);
            _pollCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeCancellation.Token);
        }

        AuthoringRunResponse initial;
        try
        {
            initial = await RefreshCoreAsync(
                navigationCancellation);
        }
        catch
        {
            ResetFailedStart();
            throw;
        }

        lock (_sync)
        {
            if (!_terminal && !_disposed)
            {
                _pollTask = PollAsync(_pollCancellation!.Token);
            }
        }
        return initial;
    }

    public Task<AuthoringRunResponse> RefreshNowAsync(
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_started)
            {
                throw new InvalidOperationException(
                    "The polling session has not been started.");
            }
        }
        return RefreshCoreAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        Task? pollTask;
        CancellationTokenSource? lifetimeCancellation;
        CancellationTokenSource? pollCancellation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            lifetimeCancellation = _lifetimeCancellation;
            pollCancellation = _pollCancellation;
            pollTask = _pollTask;
        }

        lifetimeCancellation?.Cancel();
        pollCancellation?.Cancel();
        if (pollTask is not null)
        {
            try
            {
                await pollTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _refreshGate.WaitAsync();
        _refreshGate.Release();
        _refreshGate.Dispose();
        pollCancellation?.Dispose();
        lifetimeCancellation?.Dispose();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, _timeProvider, ct);
                await RefreshCoreAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
                when (ct.IsCancellationRequested ||
                    IsLifetimeCancellationRequested())
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Authoring run polling refresh failed; polling will continue.");
            }
        }
    }

    private async Task<AuthoringRunResponse> RefreshCoreAsync(
        CancellationToken callerCancellation)
    {
        Func<CancellationToken, Task<AuthoringRunResponse>> refresh;
        CancellationToken lifetimeToken;
        lock (_sync)
        {
            ThrowIfDisposed();
            refresh = _refresh ??
                throw new InvalidOperationException(
                    "The polling session has not been started.");
            lifetimeToken = _lifetimeCancellation!.Token;
        }

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                lifetimeToken);
        await _refreshGate.WaitAsync(linked.Token);
        try
        {
            AuthoringRunResponse response =
                await refresh(linked.Token);
            if (response.Run.State?.IsTerminal == true)
            {
                MarkTerminal();
            }
            return response;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void MarkTerminal()
    {
        lock (_sync)
        {
            if (_terminal)
            {
                return;
            }
            _terminal = true;
            _pollCancellation?.Cancel();
        }
    }

    private bool IsLifetimeCancellationRequested()
    {
        lock (_sync)
        {
            return _lifetimeCancellation?.IsCancellationRequested == true;
        }
    }

    private void ResetFailedStart()
    {
        CancellationTokenSource? lifetimeCancellation;
        CancellationTokenSource? pollCancellation;
        lock (_sync)
        {
            lifetimeCancellation = _lifetimeCancellation;
            pollCancellation = _pollCancellation;
            _lifetimeCancellation = null;
            _pollCancellation = null;
            _refresh = null;
            _started = false;
            _terminal = false;
        }
        pollCancellation?.Cancel();
        lifetimeCancellation?.Cancel();
        pollCancellation?.Dispose();
        lifetimeCancellation?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
