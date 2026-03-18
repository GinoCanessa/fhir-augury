using System.Threading.Channels;

namespace FhirAugury.Service;

/// <summary>Bounded channel-based ingestion queue.</summary>
public class IngestionQueue
{
    private readonly Channel<IngestionRequest> _channel;

    public IngestionQueue(int capacity = 100)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        };
        _channel = Channel.CreateBounded<IngestionRequest>(options);
    }

    /// <summary>Current number of items in the queue.</summary>
    public int Count => _channel.Reader.Count;

    /// <summary>Enqueues an ingestion request, waiting if the queue is full.</summary>
    public async ValueTask EnqueueAsync(IngestionRequest request, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(request, ct);
    }

    /// <summary>Dequeues items as they become available.</summary>
    public IAsyncEnumerable<IngestionRequest> DequeueAllAsync(CancellationToken ct = default)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }

    /// <summary>Attempts to read a single item without waiting.</summary>
    public bool TryDequeue(out IngestionRequest? request)
    {
        return _channel.Reader.TryRead(out request);
    }
}
