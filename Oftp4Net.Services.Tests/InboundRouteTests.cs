using Oftp4Net.Services.Oftp;

namespace Oftp4Net.Services.Tests;

/// <summary>Where a received file is stored, chosen by its virtual file name.</summary>
public class InboundRouteTests
{
    private static readonly List<InboundRoute> Routes =
    [
        new() { Pattern = "XXX*", Directory = "/A" },
        new() { Pattern = "YYYY*", Directory = "/B" },
        new() { Pattern = "*.EDI", Directory = "/edi" },
    ];

    [Theory]
    [InlineData("XXX", "/A")]
    [InlineData("XXXORDERS", "/A")]
    [InlineData("xxxorders", "/A")]
    [InlineData("YYYY0001", "/B")]
    [InlineData("INVOICE.EDI", "/edi")]
    // Nothing matches: the file stays in the receive directory.
    [InlineData("YYY", null)]
    [InlineData("ORDERS", null)]
    [InlineData("OXXX", null)]
    public void FirstMatchingRuleDecides(string virtualFileName, string? expected) =>
        Assert.Equal(expected, InboundRoutes.Find(Routes, virtualFileName));

    [Fact]
    public void RulesAreReadFromTheTop()
    {
        List<InboundRoute> routes =
        [
            new() { Pattern = "ORDERS-URGENT", Directory = "/urgent" },
            new() { Pattern = "ORDERS*", Directory = "/orders" },
        ];

        Assert.Equal("/urgent", InboundRoutes.Find(routes, "ORDERS-URGENT"));
        Assert.Equal("/orders", InboundRoutes.Find(routes, "ORDERS-2026"));
    }

    [Fact]
    public void IncompleteRuleIsSkipped()
    {
        List<InboundRoute> routes =
        [
            new() { Pattern = "XXX*", Directory = "  " },
            new() { Pattern = "", Directory = "/nowhere" },
            new() { Pattern = "XXX*", Directory = " /A " },
        ];

        Assert.Equal("/A", InboundRoutes.Find(routes, "XXXORDERS"));
        Assert.Null(InboundRoutes.Find(null, "XXXORDERS"));
        Assert.Null(InboundRoutes.Find([], "XXXORDERS"));
    }

    [Fact]
    public void ArrivedFilesAreNotOverwritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "oftp4net-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string name = "ORDERS_202609231200000000";
        try
        {
            var first = InboundRoutes.UniquePath(directory, name);
            Assert.Equal(Path.Combine(directory, name), first);
            File.WriteAllText(first, "first");

            // Another partner sent a file of the same name with the same stamp.
            var second = InboundRoutes.UniquePath(directory, name);
            Assert.Equal(Path.Combine(directory, name + "_1"), second);

            // A file that is being received right now is taken as well.
            File.WriteAllText(second + ".part", "receiving");
            Assert.Equal(Path.Combine(directory, name + "_2"), InboundRoutes.UniquePath(directory, name));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("A?C", "ABC", true)]
    [InlineData("A?C", "AC", false)]
    // The characters of a regular expression are taken literally.
    [InlineData("A.C", "ABC", false)]
    [InlineData("A.C", "A.C", true)]
    public void PatternUsesWildcardsOnly(string pattern, string name, bool expected) =>
        Assert.Equal(expected, InboundRoutes.Matches(pattern, name));
}
