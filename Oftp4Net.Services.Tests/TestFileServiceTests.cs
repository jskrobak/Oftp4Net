namespace Oftp4Net.Services.Tests;

public class TestFileServiceTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var request = new TestFileRequest();

        Assert.Null(TestFileService.Validate(request));
        Assert.Equal("TEST", request.VirtualFileName);
        Assert.Equal("This is test from artipa. support@artipa.com", request.Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0")]
    [InlineData("test")]
    [InlineData("TEST*")]
    public void InvalidVirtualFileNamesAreRefused(string name)
    {
        Assert.NotNull(TestFileService.Validate(new TestFileRequest { VirtualFileName = name }));
    }

    [Theory]
    [InlineData("TEST")]
    [InlineData(" OP.TEST-FILE_1 ")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    public void ValidVirtualFileNamesAreAccepted(string name)
    {
        Assert.Null(TestFileService.Validate(new TestFileRequest { VirtualFileName = name }));
    }

    [Fact]
    public void EmptyContentIsRefused()
    {
        Assert.NotNull(TestFileService.Validate(new TestFileRequest { Content = " \r\n" }));
    }
}
