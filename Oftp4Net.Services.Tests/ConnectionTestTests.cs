using System.Net.Sockets;
using System.Security.Authentication;
using Oftp4Net.Core;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Core.Transport;
using Oftp4Net.Domain;
using Oftp4Net.Services.ConnectionTests;

namespace Oftp4Net.Services.Tests;

public class ConnectionTestTests
{
    private static readonly Partner Partner = new() { Name = "Alpha", Host = "oftp.alpha.example", Port = 6619 };

    [Fact]
    public void RefusalOfThePartnerNamesTheReason()
    {
        var (message, reasonCode) = ConnectionTestService.Describe(
            new OftpSessionAbortedException(ReasonCodes.InvalidPassword, "Password wrong "), new OftpConnectDiagnostics(), Partner);

        Assert.Equal("04", reasonCode);
        Assert.Equal("Alpha ended the session with reason 04 (invalid password): Password wrong", message);
    }

    [Fact]
    public void RefusalHereIsToldApart()
    {
        var (message, reasonCode) = ConnectionTestService.Describe(
            new OftpProtocolException(ReasonCodes.UserCodeNotKnown, "Unexpected responder X, expected Y."), new OftpConnectDiagnostics(), Partner);

        Assert.Equal("03", reasonCode);
        Assert.StartsWith("Refused here with reason 03 (user code not known)", message);
    }

    [Fact]
    public void RefusedCertificateNamesTheProblem()
    {
        var diagnostics = new OftpConnectDiagnostics();
        typeof(OftpConnectDiagnostics).GetProperty(nameof(OftpConnectDiagnostics.CertificateProblem))!
            .SetValue(diagnostics, "The certificate chain is not trusted: untrusted root.");

        var (message, _) = ConnectionTestService.Describe(new AuthenticationException("The remote certificate was rejected."), diagnostics, Partner);

        Assert.Equal("The certificate of Alpha is refused: The certificate chain is not trusted: untrusted root.", message);
    }

    [Fact]
    public void TlsFailureWithAnAcceptedCertificatePointsAtTheOtherSide()
    {
        var (message, _) = ConnectionTestService.Describe(
            new AuthenticationException("Authentication failed", new IOException("Received an unexpected EOF.")), new OftpConnectDiagnostics(), Partner);

        Assert.Contains("Received an unexpected EOF.", message);
        Assert.Contains("client certificate", message);
    }

    [Fact]
    public void NoConnectionNamesTheAddress()
    {
        var (message, _) = ConnectionTestService.Describe(new SocketException((int)SocketError.ConnectionRefused), new OftpConnectDiagnostics(), Partner);

        Assert.StartsWith("No connection to oftp.alpha.example:6619:", message);
    }

    [Theory]
    [InlineData("08", "resources not available")]
    [InlineData("12", "secure authentication requirements incompatible")]
    [InlineData("42", "unspecified")]
    public void ReasonCodesAreNamed(string code, string text)
    {
        Assert.Equal(text, ConnectionTestService.ReasonText(code));
    }
}
