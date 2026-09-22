using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Core.Protocol.Commands;
using Oftp4Net.Core.Session;
using Oftp4Net.Core.Transport;

namespace Oftp4Net.Core.Tests;

public class SessionTests
{
    private const string ClientCode = "O0013000000CLIENT";
    private const string ClientPassword = "CPWD";
    private const string ServerCode = "O0013000000SERVER";
    private const string ServerPassword = "SPWD";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static TestSessionHandler CreateClientHandler() => new(ClientCode, ClientPassword, ServerCode, ServerPassword);
    private static TestSessionHandler CreateServerHandler() => new(ServerCode, ServerPassword, ClientCode, ClientPassword);

    /// <summary>Runs a responder behind a listener and an initiator connecting to it, returns both outcomes.</summary>
    private static async Task<(Exception? Client, Exception? Server)> RunAsync(
        TestSessionHandler client, TestSessionHandler server,
        int bufferSize = 4096, int credit = 64,
        string clientPassword = ClientPassword,
        OftpTlsOptions? serverTls = null, OftpTlsOptions? clientTls = null)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var serverDone = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var listener = new OftpListener(new IPEndPoint(IPAddress.Loopback, 0), serverTls,
            async (transport, _, ct) =>
            {
                try
                {
                    var session = new OftpSession(transport,
                        new OftpSessionOptions { Role = OftpRole.Responder, ExchangeBufferSize = bufferSize, Credit = credit },
                        server, NullLogger.Instance);
                    await session.RunAsync(ct);
                    serverDone.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    serverDone.TrySetResult(ex);
                }
            }, NullLogger.Instance);
        listener.Start();

        Exception? clientError = null;
        try
        {
            await using var transport = await OftpConnector.ConnectAsync("localhost", listener.LocalEndPoint.Port, clientTls, cts.Token);
            var session = new OftpSession(transport, new OftpSessionOptions
            {
                Role = OftpRole.Initiator,
                LocalCode = ClientCode,
                LocalPassword = clientPassword,
                ExchangeBufferSize = bufferSize,
                Credit = credit,
            }, client, NullLogger.Instance);
            await session.RunAsync(cts.Token);
        }
        catch (Exception ex)
        {
            clientError = ex;
        }

        var serverError = await serverDone.Task.WaitAsync(cts.Token);
        return (clientError, serverError);
    }

    [Fact]
    public async Task FilesAreTransferredAndConfirmedWithEerp()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        var large = RandomNumberGenerator.GetBytes(200_000);
        client.Enqueue("LARGE", large, ServerCode);
        client.Enqueue("EMPTY", [], ServerCode);

        // Small buffers and credit force many DATA buffers and CDT round trips.
        var (clientError, serverError) = await RunAsync(client, server, bufferSize: 128, credit: 2);

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal(large, server.ReceivedFiles["LARGE"]);
        Assert.Empty(server.ReceivedFiles["EMPTY"]);
        Assert.Equal(["LARGE", "EMPTY"], client.SentFiles);

        var eerps = client.ReceivedEndResponses.Cast<EERP>().ToList();
        Assert.Equal(2, eerps.Count);
        Assert.Contains(eerps, e => e is { DatasetName: "LARGE", Destination: ClientCode, Originator: ServerCode });
    }

    [Fact]
    public async Task FilesAreTransferredInBothDirections()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        client.Enqueue("UP", "hello server"u8.ToArray(), ServerCode);
        server.Enqueue("DOWN", "hello client"u8.ToArray(), ClientCode);

        var (clientError, serverError) = await RunAsync(client, server);

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal("hello server"u8.ToArray(), server.ReceivedFiles["UP"]);
        Assert.Equal("hello client"u8.ToArray(), client.ReceivedFiles["DOWN"]);
        Assert.Single(client.ReceivedEndResponses);
        Assert.Single(server.ReceivedEndResponses);
    }

    [Fact]
    public async Task EmptySessionEndsNormally()
    {
        var (clientError, serverError) = await RunAsync(CreateClientHandler(), CreateServerHandler());

        Assert.Null(clientError);
        Assert.Null(serverError);
    }

    [Fact]
    public async Task InvalidPasswordEndsSessionWithReasonCode04()
    {
        var (clientError, serverError) = await RunAsync(CreateClientHandler(), CreateServerHandler(), clientPassword: "WRONG");

        var aborted = Assert.IsType<OftpSessionAbortedException>(clientError);
        Assert.Equal(ReasonCodes.InvalidPassword, aborted.ReasonCode);
        Assert.Equal(ReasonCodes.InvalidPassword, Assert.IsType<OftpProtocolException>(serverError).ReasonCode);
    }

    [Fact]
    public async Task RefusedFileIsReportedToSender()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        server.RefuseDatasetNames.Add("DUP");
        client.Enqueue("DUP", [1, 2, 3], ServerCode);
        client.Enqueue("OK", [4, 5, 6], ServerCode);

        var (clientError, serverError) = await RunAsync(client, server);

        Assert.Null(clientError);
        Assert.Null(serverError);
        var (name, answer) = Assert.Single(client.RefusedFiles);
        Assert.Equal("DUP", name);
        Assert.Equal(AnswerReasonCodes.DuplicateFile, answer.ReasonCode);
        Assert.True(answer.RetryLater);
        Assert.Equal(["OK"], client.SentFiles);
    }

    [Fact]
    public async Task TlsWithPinnedCertificate()
    {
        using var certificate = CreateSelfSignedCertificate();
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        client.Enqueue("SECURE", "over tls"u8.ToArray(), ServerCode);

        var (clientError, serverError) = await RunAsync(client, server,
            serverTls: new OftpTlsOptions { LocalCertificate = certificate },
            clientTls: new OftpTlsOptions
            {
                TrustedCertificates = [X509CertificateLoader.LoadCertificate(certificate.RawData)]
            });

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal("over tls"u8.ToArray(), server.ReceivedFiles["SECURE"]);
    }

    [Fact]
    public async Task MutualTlsWithPinnedClientCertificate()
    {
        using var serverCertificate = CreateSelfSignedCertificate();
        using var clientCertificate = CreateSelfSignedCertificate();
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        client.Enqueue("MTLS", "mutual tls"u8.ToArray(), ServerCode);

        var (clientError, serverError) = await RunAsync(client, server,
            serverTls: new OftpTlsOptions
            {
                LocalCertificate = serverCertificate,
                RequireClientCertificate = true,
                TrustedCertificates = [X509CertificateLoader.LoadCertificate(clientCertificate.RawData)]
            },
            clientTls: new OftpTlsOptions
            {
                LocalCertificate = clientCertificate,
                TrustedCertificates = [X509CertificateLoader.LoadCertificate(serverCertificate.RawData)]
            });

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal("mutual tls"u8.ToArray(), server.ReceivedFiles["MTLS"]);
    }

    [Fact]
    public async Task TlsWithUntrustedCertificateIsRejected()
    {
        using var certificate = CreateSelfSignedCertificate();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var listener = new OftpListener(new IPEndPoint(IPAddress.Loopback, 0),
            new OftpTlsOptions { LocalCertificate = certificate },
            (_, _, _) => Task.CompletedTask, NullLogger.Instance);
        listener.Start();

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            OftpConnector.ConnectAsync("localhost", listener.LocalEndPoint.Port, new OftpTlsOptions(), cts.Token));
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        // Round trip through PKCS#12 so the private key is usable by SslStream on all platforms.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }
}
