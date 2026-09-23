using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.Domain;
using Oftp4Net.Services.Health;
using Oftp4Net.Services.Security;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services.Tests;

public class HealthCheckTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0);

    [Fact]
    public void ListenerThatDidNotStartIsUnhealthy()
    {
        var result = ListenersHealthCheck.Evaluate(
        [
            new ListenerStatus(1, "TLS", "0.0.0.0:6619", true, null, 0),
            new ListenerStatus(2, "TCP", "0.0.0.0:3305", false, "Address already in use", 0),
        ]);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("TCP", result.Description);
        Assert.Contains("Address already in use", result.Description);
    }

    [Fact]
    public void NoListenerIsHealthy()
    {
        Assert.Equal(HealthStatus.Healthy, ListenersHealthCheck.Evaluate([]).Status);
    }

    [Fact]
    public void StoppedSendServiceIsUnhealthy()
    {
        var result = SendServiceHealthCheck.Evaluate(false, false, Now, Now, 0, 60, Now);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void PausedSendServiceIsDegraded()
    {
        var result = SendServiceHealthCheck.Evaluate(true, true, Now.AddDays(-1), Now.AddDays(-1), 0, 60, Now);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Theory]
    [InlineData(239, HealthStatus.Healthy)]
    [InlineData(241, HealthStatus.Unhealthy)]
    public void SendServiceThatStoppedProcessingTheQueueIsUnhealthy(int secondsSinceLastRun, HealthStatus expected)
    {
        // Three intervals of 60 s and a minute.
        var result = SendServiceHealthCheck.Evaluate(true, false, Now.AddHours(-1), Now.AddSeconds(-secondsSinceLastRun), 0, 60, Now);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void SendServiceIsMeasuredFromItsStartBeforeTheFirstRun()
    {
        Assert.Equal(HealthStatus.Healthy, SendServiceHealthCheck.Evaluate(true, false, Now.AddSeconds(-10), null, 0, 60, Now).Status);
        Assert.Equal(HealthStatus.Unhealthy, SendServiceHealthCheck.Evaluate(true, false, Now.AddHours(-1), null, 0, 60, Now).Status);
    }

    [Fact]
    public void StorageThatCannotBeWrittenIsUnhealthy()
    {
        var result = StorageHealthCheck.Evaluate(
        [
            new StorageHealthCheck.DirectoryProbe("receive directory", "/data/received", null, 10L << 30),
            new StorageHealthCheck.DirectoryProbe("outbox directory", "/data/outbox", "Permission denied", null),
        ], 1L << 30);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("/data/outbox", result.Description);
    }

    [Fact]
    public void FullDiskIsDegraded()
    {
        var result = StorageHealthCheck.Evaluate(
            [new StorageHealthCheck.DirectoryProbe("receive directory", "/data/received", null, 100L << 20)], 1L << 30);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public void ProbeWritesToTheDirectoryAndLeavesNothingBehind()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"oftp-health-{Guid.NewGuid():N}");
        try
        {
            var probe = StorageHealthCheck.Probe("receive directory", directory);

            Assert.Null(probe.Error);
            Assert.True(probe.FreeBytes > 0);
            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ProbeOfADirectoryThatCannotBeCreatedFails()
    {
        var file = Path.GetTempFileName();
        try
        {
            // A directory cannot be created under a file.
            var probe = StorageHealthCheck.Probe("outbox directory", Path.Combine(file, "outbox"));

            Assert.NotNull(probe.Error);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void CertificateThatExpiresSoonIsDegraded()
    {
        var result = CertificatesHealthCheck.Evaluate(
        [
            new CertificatesHealthCheck.CertificateUse(Certificate(1, "ours", Now.AddDays(10)), "TLS client certificate"),
            new CertificatesHealthCheck.CertificateUse(Certificate(1, "ours", Now.AddDays(10)), "file security certificate"),
            new CertificatesHealthCheck.CertificateUse(Certificate(2, "partner", Now.AddYears(2)), "file security certificate of partner P"),
        ], Now);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("ours (TLS client certificate, file security certificate) expires", result.Description);
        Assert.DoesNotContain("partner", result.Description);
    }

    [Fact]
    public void ExpiredCertificateIsDegraded()
    {
        var result = CertificatesHealthCheck.Evaluate(
            [new CertificatesHealthCheck.CertificateUse(Certificate(1, "old", Now.AddDays(-1)), "server certificate of listener L")], Now);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("expired", result.Description);
    }

    [Fact]
    public void CertificatesValidLongEnoughAreHealthy()
    {
        var result = CertificatesHealthCheck.Evaluate(
            [new CertificatesHealthCheck.CertificateUse(Certificate(1, "ours", Now.AddDays(31)), "TLS client certificate")], Now);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void RevocationListOlderThanTheMaximumAgeIsDegraded()
    {
        var result = RevocationListsHealthCheck.Evaluate(true,
        [
            new CrlListState("http://ca.example/current.crl", Now.AddDays(-1), true, null),
            new CrlListState("http://ca.example/old.crl", Now.AddDays(-16), true, "timeout"),
        ], 15, Now);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("old.crl", result.Description);
        Assert.DoesNotContain("current.crl", result.Description);
    }

    [Fact]
    public void RevocationListThatWasNeverReadIsDegraded()
    {
        var result = RevocationListsHealthCheck.Evaluate(true,
            [new CrlListState("http://ca.example/ca.crl", Now, false, "404 Not Found")], 15, Now);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("404 Not Found", result.Description);
    }

    [Fact]
    public void RevocationListsAreNotCheckedWhenTheCheckIsOff()
    {
        var result = RevocationListsHealthCheck.Evaluate(false,
            [new CrlListState("http://ca.example/ca.crl", Now, false, "404 Not Found")], 15, Now);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void TrustListThatCannotBeDownloadedIsDegraded()
    {
        var status = new TslStatus { CheckedDate = Now, Error = "Name or service not known" };

        Assert.Equal(HealthStatus.Degraded, TrustListHealthCheck.Evaluate(true, status).Status);
        Assert.Equal(HealthStatus.Healthy, TrustListHealthCheck.Evaluate(false, status).Status);
    }

    [Fact]
    public void TrustListIsHealthyBeforeTheFirstDownload()
    {
        Assert.Equal(HealthStatus.Healthy, TrustListHealthCheck.Evaluate(true, new TslStatus()).Status);
    }

    [Fact]
    public void StuckFilesAreDegraded()
    {
        Assert.Equal(HealthStatus.Healthy, SendQueueHealthCheck.Evaluate(0, 0, 0).Status);

        var result = SendQueueHealthCheck.Evaluate(2, 0, 1);
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("2 file(s) have been waiting", result.Description);
        Assert.Contains("1 received file(s)", result.Description);
    }

    [Fact]
    public void AlmostFullQueueIsDegraded()
    {
        Assert.Equal(HealthStatus.Healthy, InternalQueuesHealthCheck.Evaluate([new QueueState("Hook", 799, 1000, 5)]).Status);
        Assert.Equal(HealthStatus.Degraded, InternalQueuesHealthCheck.Evaluate([new QueueState("Hook", 800, 1000, 0)]).Status);
    }

    [Fact]
    public void MonitoredQueueCountsTheItemsItDrops()
    {
        var reported = new List<long>();
        var queue = new MonitoredQueue<int>("Test", 2, BoundedChannelFullMode.DropWrite, reported.Add);

        for (var i = 0; i < 5; i++)
            queue.Writer.TryWrite(i);

        Assert.Equal(new QueueState("Test", 2, 2, 3), queue.State);
        // The first drop is reported, the next ones only every thousandth.
        Assert.Equal([1L], reported);
    }

    [Fact]
    public void MonitorReportsOnlyChangesAndAQuietStart()
    {
        var healthyStart = Report(("database", HealthStatus.Healthy), ("certificates", HealthStatus.Healthy));
        Assert.Empty(HealthMonitor.Changes(null, healthyStart));

        var degraded = Report(("database", HealthStatus.Healthy), ("certificates", HealthStatus.Degraded));
        var change = Assert.Single(HealthMonitor.Changes(healthyStart, degraded));
        Assert.Equal(new HealthMonitor.Change("certificates", HealthStatus.Healthy, HealthStatus.Degraded, "certificates is Degraded"), change);

        Assert.Empty(HealthMonitor.Changes(degraded, degraded));
        Assert.Single(HealthMonitor.Changes(degraded, healthyStart));
    }

    [Fact]
    public void MonitorReportsAProblemAtTheStart()
    {
        var change = Assert.Single(HealthMonitor.Changes(null, Report(("database", HealthStatus.Unhealthy))));

        Assert.Null(change.Previous);
        Assert.Equal(HealthStatus.Unhealthy, change.Current);
    }

    [Fact]
    public void WebhookNamesTheChecksThatAreNotHealthy()
    {
        var report = Report(("database", HealthStatus.Healthy), ("certificates", HealthStatus.Degraded), ("listeners", HealthStatus.Unhealthy));

        Assert.Equal("certificates: certificates is Degraded\nlisteners: listeners is Unhealthy", HealthMonitor.Problems(report));
        Assert.Null(HealthMonitor.Problems(Report(("database", HealthStatus.Healthy))));
    }

    private static Certificate Certificate(int id, string name, DateTime validTo) =>
        new() { Id = id, Name = name, ValidFrom = validTo.AddYears(-3), ValidTo = validTo };

    private static HealthReport Report(params (string Name, HealthStatus Status)[] entries) =>
        new(entries.ToDictionary(e => e.Name,
            e => new HealthReportEntry(e.Status, $"{e.Name} is {e.Status}", TimeSpan.Zero, null, null)), TimeSpan.Zero);
}
