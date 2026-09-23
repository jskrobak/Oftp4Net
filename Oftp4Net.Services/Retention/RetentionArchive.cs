using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oftp4Net.Services.Retention;

/// <summary>
/// Writes removed records to files per kind and month (<c>transfer-log-2026-09.jsonl.gz</c>): one JSON object per
/// line, gzip compressed. Every batch is appended as a gzip member of its own, which <c>zcat</c> and any gzip
/// reader read as one stream, so a file never has to be rewritten.
/// </summary>
public static class RetentionArchive
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FileName(string kind, DateTime month) => $"{kind}-{month:yyyy-MM}.jsonl.gz";

    /// <summary>
    /// Appends the records to the files of their months and makes sure they are on the disk before it returns,
    /// so that they can be removed from the database afterwards.
    /// </summary>
    public static async Task AppendAsync<T>(string directory, string kind, IEnumerable<T> records, Func<T, DateTime> month,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        foreach (var group in records.GroupBy(r => new DateTime(month(r).Year, month(r).Month, 1)))
        {
            await using var file = new FileStream(Path.Combine(directory, FileName(kind, group.Key)), FileMode.Append,
                FileAccess.Write, FileShare.Read, 81920, useAsync: true);
            await using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
            await using (var writer = new StreamWriter(gzip))
            {
                foreach (var record in group)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonOptions).AsMemory(), cancellationToken);
            }

            file.Flush(flushToDisk: true);
        }
    }

    /// <summary>Reads an archive file back, e.g. to look for a record or in tests.</summary>
    public static async IAsyncEnumerable<JsonElement> ReadAsync(string path)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        while (await reader.ReadLineAsync() is { } line)
            yield return JsonSerializer.Deserialize<JsonElement>(line);
    }
}
