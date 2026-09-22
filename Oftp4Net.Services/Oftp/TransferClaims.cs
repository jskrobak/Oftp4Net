using System.Collections.Concurrent;

namespace Oftp4Net.Services.Oftp;

/// <summary>
/// Prevents two concurrent sessions (e.g. an outgoing connection and an incoming one from the same partner)
/// from sending the same queue item or the same End to End Response.
/// </summary>
public sealed class TransferClaims
{
    private readonly ConcurrentDictionary<string, bool> _claims = new();

    public bool TryClaim(string key) => _claims.TryAdd(key, true);

    public void Release(string key) => _claims.TryRemove(key, out _);

    public static string ForSendQueueItem(int id) => $"send:{id}";

    public static string ForReceivedFile(int id) => $"eerp:{id}";
}
