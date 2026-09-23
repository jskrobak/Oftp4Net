using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.Domain;
using Oftp4Net.Services.Health;
using Oftp4Net.Services.Retention;

namespace Oftp4Net.Services.Tests;

public class RetentionTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 3, 0, 0);

    [Fact]
    public void CutoffsFollowTheSettings()
    {
        var cutoffs = RetentionCutoffs.From(new GlobalSettings(), Now);

        Assert.Equal(new RetentionCutoffs(Now.AddDays(-30), Now.AddDays(-90), Now.AddDays(-365), Now.AddDays(-365)), cutoffs);
    }

    [Fact]
    public void RecordsAreNotDeletedBeforeTheirContentIsArchived()
    {
        var cutoffs = RetentionCutoffs.From(new GlobalSettings
        {
            DeleteContentAfterDays = 60,
            DeleteInformationEventsAfterDays = 14,
            DeleteEventsAfterDays = 30,
            DeleteFinishedFilesAfterDays = 90,
        }, Now);

        Assert.Equal(Now.AddDays(-60), cutoffs.InformationEvents);
        Assert.Equal(Now.AddDays(-60), cutoffs.Events);
    }

    [Fact]
    public void FinishedFilesAreKeptLongEnoughToRecogniseDuplicates()
    {
        var cutoffs = RetentionCutoffs.From(new GlobalSettings { DeleteFinishedFilesAfterDays = 1 }, Now);

        Assert.Equal(Now.AddDays(-RetentionCutoffs.MinFinishedFilesDays), cutoffs.FinishedFiles);
    }

    [Fact]
    public async Task ArchiveAppendsToTheFileOfTheMonthAndReadsBackEverything()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"oftp-archive-{Guid.NewGuid():N}");
        try
        {
            TransferEvent Event(int id, DateTime timestamp) => new()
            {
                Id = id, Timestamp = timestamp, Level = TransferEventLevel.Error, Message = $"event {id}", Details = "stack trace",
            };

            await RetentionArchive.AppendAsync(directory, "transfer-log", [Event(1, new(2026, 8, 31, 23, 0, 0)), Event(2, new(2026, 9, 1))], e => e.Timestamp);
            // A second batch is a gzip member of its own in the same file.
            await RetentionArchive.AppendAsync(directory, "transfer-log", [Event(3, new(2026, 9, 2))], e => e.Timestamp);

            Assert.Equal(["transfer-log-2026-08.jsonl.gz", "transfer-log-2026-09.jsonl.gz"],
                Directory.GetFiles(directory).Select(Path.GetFileName).Order());

            var september = await RetentionArchive.ReadAsync(Path.Combine(directory, "transfer-log-2026-09.jsonl.gz")).ToListAsync();
            Assert.Equal([2, 3], september.Select(e => e.GetProperty("id").GetInt32()));
            Assert.Equal("Error", september[0].GetProperty("level").GetString());
            Assert.Equal("stack trace", september[0].GetProperty("details").GetString());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ArchivedSendQueueItemCarriesThePartnerButNotTheWebhookSecret()
    {
        var item = new SendQueueItem
        {
            Id = 7,
            Partner = new Partner { Name = "Alpha", SSID = "O0013000000ALPHA" },
            Identity = new Identity { SSID = "O0013000000US" },
            VirtualFileName = "INVOIC",
            Status = SendStatus.DELIVERED,
            WebhookUrl = "https://erp.example/hook",
            WebhookSecret = "s3cret",
            ContentHash = [1, 2, 3],
        };

        var json = JsonSerializer.Serialize(ArchivedSendQueueItem.From(item));

        Assert.Contains("O0013000000ALPHA", json);
        Assert.Contains("https://erp.example/hook", json);
        Assert.DoesNotContain("s3cret", json);
    }

    [Fact]
    public void RetentionThatFailedIsDegraded()
    {
        var result = new RetentionResult(Now, Now, 0, 0, 0, 0, 0, "Archiving the transfer log failed: disk full");

        var health = RetentionHealthCheck.Evaluate(true, Now.AddDays(-5), result, Now.AddDays(-1), Now);

        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Contains("disk full", health.Description);
    }

    [Fact]
    public void RetentionThatHasNotRunForTwoDaysIsDegraded()
    {
        var ok = new RetentionResult(Now.AddDays(-3), Now.AddDays(-3), 1, 0, 0, 0, 0, null);

        Assert.Equal(HealthStatus.Degraded, RetentionHealthCheck.Evaluate(true, Now.AddDays(-5), ok, Now.AddDays(-3), Now).Status);
        Assert.Equal(HealthStatus.Healthy, RetentionHealthCheck.Evaluate(true, Now.AddDays(-5), ok, Now.AddDays(-1), Now).Status);
        Assert.Equal(HealthStatus.Healthy, RetentionHealthCheck.Evaluate(true, Now.AddMinutes(-1), null, null, Now).Status);
        Assert.Equal(HealthStatus.Healthy, RetentionHealthCheck.Evaluate(false, Now.AddDays(-5), null, null, Now).Status);
    }
}
