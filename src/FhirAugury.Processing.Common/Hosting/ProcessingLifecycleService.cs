using FhirAugury.Processing.Common.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Hosting;

/// <summary>
/// In-memory lifecycle state for a Processing service.
/// </summary>
public class ProcessingLifecycleService
{
    private readonly object _lock = new();
    private bool _isRunning;
    private DateTimeOffset _startedAt;

    public ProcessingLifecycleService(IOptions<ProcessingServiceOptions> optionsAccessor)
    {
        ProcessingServiceOptions options = optionsAccessor.Value;
        _isRunning = options.StartProcessingOnStartup;
        _startedAt = DateTimeOffset.UtcNow;
    }

    public bool IsRunning { get { lock (_lock) { return _isRunning; } } }
    public bool IsPaused => !IsRunning;
    public DateTimeOffset StartedAt { get { lock (_lock) { return _startedAt; } } }
    public DateTimeOffset? LastPollAt { get; private set; }
    public string? LastError { get; private set; }

    public void Start()
    {
        lock (_lock)
        {
            if (!_isRunning)
            {
                _isRunning = true;
                _startedAt = DateTimeOffset.UtcNow;
            }
            LastError = null;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isRunning = false;
        }
    }

    public void RecordPoll(DateTimeOffset pollAt)
    {
        LastPollAt = pollAt;
    }

    public void RecordError(string? error)
    {
        LastError = error;
    }
}
