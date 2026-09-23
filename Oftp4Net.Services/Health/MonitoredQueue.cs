using System.Threading.Channels;

namespace Oftp4Net.Services.Health;

/// <summary>Fill of an in-memory queue of a background service and what it had to throw away since the start.</summary>
public sealed record QueueState(string Name, int Count, int Capacity, long Dropped);

/// <summary>
/// A bounded channel of a background service that counts the items it drops when it is full, so that the health
/// check can tell. A full channel in <see cref="BoundedChannelFullMode.DropWrite"/> mode accepts a write and throws
/// the item away, so without the count nobody would notice.
/// </summary>
public sealed class MonitoredQueue<T>
{
    /// <summary>Drops are reported for the first one and then for every this many, not for each of them.</summary>
    private const int ReportEvery = 1000;

    private long _dropped;

    /// <param name="dropped">Called with the number of items dropped so far, for the first one and every thousandth.</param>
    public MonitoredQueue(string name, int capacity, BoundedChannelFullMode fullMode, Action<long>? dropped = null)
    {
        Name = name;
        Capacity = capacity;
        Channel = System.Threading.Channels.Channel.CreateBounded<T>(
            new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = fullMode },
            _ =>
            {
                var count = Interlocked.Increment(ref _dropped);
                if (count == 1 || count % ReportEvery == 0)
                    dropped?.Invoke(count);
            });
    }

    public string Name { get; }
    public int Capacity { get; }
    public Channel<T> Channel { get; }
    public ChannelReader<T> Reader => Channel.Reader;
    public ChannelWriter<T> Writer => Channel.Writer;

    public QueueState State => new(Name, Channel.Reader.Count, Capacity, Interlocked.Read(ref _dropped));
}
