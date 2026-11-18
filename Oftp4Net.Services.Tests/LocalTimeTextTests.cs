namespace Oftp4Net.Services.Tests;

public class LocalTimeTextTests
{
    [Theory]
    [InlineData("2026-12-03 10:00")]
    [InlineData(" 2026-12-03 10:00 ")]
    [InlineData("2026-12-03T10:00")]
    [InlineData("3.12.2026 10:00")]
    [InlineData("3. 12. 2026 10:00")]
    [InlineData("03.12.2026 10:00")]
    public void ReadsTheUsualForms(string text)
    {
        Assert.True(LocalTimeText.TryParse(text, out var time));
        Assert.Equal(new DateTime(2026, 12, 3, 10, 0, 0), time);
    }

    [Fact]
    public void ADateAloneIsMidnight()
    {
        Assert.True(LocalTimeText.TryParse("2026-12-31", out var time));
        Assert.Equal(new DateTime(2026, 12, 31), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12/3/2026 10:00")]
    [InlineData("2026-12-32 10:00")]
    [InlineData("tomorrow")]
    public void RefusesAnythingElse(string? text) => Assert.False(LocalTimeText.TryParse(text, out _));

    [Fact]
    public void WritesWhatItReads()
    {
        var time = new DateTime(2026, 12, 3, 10, 0, 0);

        Assert.True(LocalTimeText.TryParse(LocalTimeText.ToText(time), out var read));
        Assert.Equal(time, read);
    }
}
