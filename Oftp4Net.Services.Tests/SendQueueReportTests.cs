using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Health;

namespace Oftp4Net.Services.Tests;

public class SendQueueReportTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0);

    private static readonly Partner Alpha = new() { Id = 1, Name = "Alpha", SSID = "O0013000000ALPHA" };
    private static readonly Partner Beta = new() { Id = 2, Name = "beta", SSID = "O0013000000BETA" };
    private static readonly Partner Gamma = new() { Id = 3, Name = "Gamma", SSID = "O0013000000GAMMA" };

    [Fact]
    public void EveryPartnerIsListedAlsoWithAnEmptyQueue()
    {
        var report = SendQueueReportService.Build([Gamma, Beta, Alpha], [], Now);

        Assert.Equal(["Alpha", "beta", "Gamma"], report.Partners.Select(p => p.Partner));
        Assert.All(report.Partners, p =>
        {
            Assert.Equal(0, p.Waiting);
            Assert.Equal(0, p.OldestWaitingMinutes);
            Assert.Null(p.LastError);
        });
        Assert.Equal(new SendQueueReport(0, 0, 0, 0, 0, report.Partners), report);
    }

    [Fact]
    public void AgesAreWholeMinutesAndTotalsAddUp()
    {
        var report = SendQueueReportService.Build([Alpha, Beta],
        [
            new SendQueuePartnerState(1, 3, Now.AddMinutes(-90.5), 1, 2, Now.AddHours(-5), "connection refused", Now.AddMinutes(-2)),
            new SendQueuePartnerState(2, 1, Now.AddMinutes(-10), 0, 0, null, null, null),
        ], Now);

        Assert.Equal(new SendQueueReport(4, 90, 1, 2, 300, report.Partners), report);

        var alpha = report.Partners[0];
        Assert.Equal(new SendQueuePartnerReport("Alpha", "O0013000000ALPHA", 3, 90, 1, 2, 300, "connection refused", Now.AddMinutes(-2)), alpha);

        var beta = report.Partners[1];
        Assert.Equal(10, beta.OldestWaitingMinutes);
        Assert.Equal(0, beta.OldestAwaitingEndResponseMinutes);
    }

    [Fact]
    public void ItemsOfAPartnerThatNoLongerExistsCountInTheTotals()
    {
        var report = SendQueueReportService.Build([Alpha],
            [new SendQueuePartnerState(99, 2, Now.AddMinutes(-30), 0, 0, null, null, null)], Now);

        Assert.Equal(2, report.Waiting);
        Assert.Equal(30, report.OldestWaitingMinutes);
        Assert.Equal(0, Assert.Single(report.Partners).Waiting);
    }

    [Fact]
    public void ATimeInTheFutureIsNoAge()
    {
        var report = SendQueueReportService.Build([Alpha],
            [new SendQueuePartnerState(1, 1, Now.AddMinutes(5), 0, 0, null, null, null)], Now);

        Assert.Equal(0, report.OldestWaitingMinutes);
    }
}
