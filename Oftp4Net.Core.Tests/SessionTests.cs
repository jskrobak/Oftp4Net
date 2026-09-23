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
        OftpTlsOptions? serverTls = null, OftpTlsOptions? clientTls = null,
        bool secureAuthentication = false, bool bufferCompression = false, bool restart = false,
        int level = ProtocolLevels.Oftp2, int? responderLevel = null)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var serverDone = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var listener = new OftpListener(new IPEndPoint(IPAddress.Loopback, 0), serverTls,
            async (transport, _, ct) =>
            {
                try
                {
                    var session = new OftpSession(transport,
                        new OftpSessionOptions
                        {
                            Role = OftpRole.Responder,
                            ExchangeBufferSize = bufferSize,
                            Credit = credit,
                            SecureAuthentication = secureAuthentication,
                            BufferCompression = bufferCompression,
                            Restart = restart,
                            ProtocolLevel = responderLevel ?? level,
                        },
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
                SecureAuthentication = secureAuthentication,
                BufferCompression = bufferCompression,
                Restart = restart,
                ProtocolLevel = level,
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
    public async Task InterruptedTransferContinuesAtTheAgreedBlock()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        var content = RandomNumberGenerator.GetBytes(3 * OftpSession.RestartBlockSize + 100);
        // The sender believes two blocks arrived last time, the receiver really has one and a half.
        client.Enqueue("PARTIAL", content, ServerCode, restartPosition: 2);
        server.PartiallyReceived["PARTIAL"] = content[..(OftpSession.RestartBlockSize + 500)];

        var (clientError, serverError) = await RunAsync(client, server, bufferSize: 512, credit: 2, restart: true);

        Assert.Null(clientError);
        Assert.Null(serverError);
        // The receiver kept one complete block, so the transfer continued there and the file is complete.
        Assert.Equal(2, server.ReceivedHeaders["PARTIAL"].RestartPosition);
        Assert.Equal(("PARTIAL", 1L), Assert.Single(client.SentFilesWithRestart));
        Assert.Equal(content, server.ReceivedFiles["PARTIAL"]);
    }

    [Fact]
    public async Task RestartPositionIsIgnoredWhenRestartIsNotAgreed()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        var content = RandomNumberGenerator.GetBytes(2 * OftpSession.RestartBlockSize);
        client.Enqueue("FULL", content, ServerCode, restartPosition: 1);
        server.PartiallyReceived["FULL"] = content[..OftpSession.RestartBlockSize];

        var (clientError, serverError) = await RunAsync(client, server);

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal(0, server.ReceivedHeaders["FULL"].RestartPosition);
        Assert.Equal(("FULL", 0L), Assert.Single(client.SentFilesWithRestart));
        Assert.Equal(content, server.ReceivedFiles["FULL"]);
    }

    [Fact]
    public async Task CompressedBuffersCarryTheSameContent()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        // Content with long runs of equal octets, which is what buffer compression is for.
        var content = new byte[50_000];
        Array.Fill(content, (byte)' ', 0, 40_000);
        RandomNumberGenerator.Fill(content.AsSpan(40_000));
        client.Enqueue("PADDED", content, ServerCode);

        var (clientError, serverError) = await RunAsync(client, server, bufferSize: 512, credit: 4, bufferCompression: true);

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal(content, server.ReceivedFiles["PADDED"]);
    }

    [Fact]
    public async Task SecureAuthenticationChallengesBothSides()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        client.Enqueue("AUTHED", "content"u8.ToArray(), ServerCode);

        var (clientError, serverError) = await RunAsync(client, server, secureAuthentication: true);

        Assert.Null(clientError);
        Assert.Null(serverError);
        // Each side challenges the other once and answers the challenge it received.
        Assert.Single(client.EncryptedChallenges);
        Assert.Single(client.DecryptedChallenges);
        Assert.Equal(client.EncryptedChallenges[0], server.DecryptedChallenges[0]);
        Assert.Equal(server.EncryptedChallenges[0], client.DecryptedChallenges[0]);
        Assert.Equal(SecureAuthentication.ChallengeLength, client.EncryptedChallenges[0].Length);
        // The session continues with the file transfer.
        Assert.Equal("content"u8.ToArray(), server.ReceivedFiles["AUTHED"]);
    }

    [Fact]
    public async Task WrongAnswerToTheChallengeEndsSessionWithReasonCode11()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        client.AnswerChallengeWrongly = true;

        var (clientError, serverError) = await RunAsync(client, server, secureAuthentication: true);

        Assert.Equal(ReasonCodes.InvalidChallengeResponse, Assert.IsType<OftpProtocolException>(serverError).ReasonCode);
        Assert.NotNull(clientError);
    }

    [Fact]
    public async Task DifferentAuthenticationRequirementsEndSessionWithReasonCode12()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        // The partner is configured without secure authentication on the responder side.
        server.SecureAuthentication = false;

        var (clientError, serverError) = await RunAsync(client, server, secureAuthentication: true);

        Assert.Equal(ReasonCodes.SecureAuthenticationRequirementsIncompatible,
            Assert.IsType<OftpProtocolException>(serverError).ReasonCode);
        Assert.NotNull(clientError);
    }

    [Fact]
    public async Task FileSecurityAttributesAreTransferredInStartFile()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        // Content the application already signed, compressed and encrypted; the session only announces it.
        var secured = RandomNumberGenerator.GetBytes(3000);
        client.Enqueue("SECURED", secured, ServerCode,
            securityLevel: SecurityLevels.EncryptedAndSigned,
            cipherSuite: CipherSuites.Aes256Sha256,
            compression: FileCompressionAlgorithms.Zlib,
            enveloping: FileEnvelopingFormats.Cms,
            signedEerpRequested: true,
            originalSize: 10_000);

        var (clientError, serverError) = await RunAsync(client, server);

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal(secured, server.ReceivedFiles["SECURED"]);

        var header = server.ReceivedHeaders["SECURED"];
        Assert.Equal(SecurityLevels.EncryptedAndSigned, header.SecurityLevel);
        Assert.Equal(CipherSuites.Aes256Sha256, header.CipherSuite);
        Assert.Equal(FileCompressionAlgorithms.Zlib, header.Compression);
        Assert.Equal(FileEnvelopingFormats.Cms, header.Enveloping);
        Assert.True(header.SignedEerpRequested);
        // Sizes are announced in 1K blocks, rounded up.
        Assert.Equal(3, header.FileSize);
        Assert.Equal(10, header.OriginalFileSize);
    }

    [Theory]
    [InlineData(ProtocolLevels.Oftp12)]
    [InlineData(ProtocolLevels.Oftp13)]
    [InlineData(ProtocolLevels.Oftp14)]
    public async Task FilesAreTransferredWithOldProtocolLevels(int level)
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        var content = RandomNumberGenerator.GetBytes(5000);
        client.Enqueue("OLDLEVEL", content, ServerCode);

        var (clientError, serverError) = await RunAsync(client, server, bufferSize: 512, credit: 3, level: level);

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal(content, server.ReceivedFiles["OLDLEVEL"]);

        // Before revision 1.4 the stamps are YYMMDD and HHMMSS, from 1.4 on CCYYMMDD and HHMMSScccc.
        var header = server.ReceivedHeaders["OLDLEVEL"];
        Assert.Equal(ProtocolLevels.HasExtendedTimestamp(level) ? 8 : 6, header.Date.Length);
        Assert.Equal(ProtocolLevels.HasExtendedTimestamp(level) ? 10 : 6, header.Time.Length);

        // The End to End Response refers to the file with the same stamps.
        var eerp = Assert.IsType<EERP>(Assert.Single(client.ReceivedEndResponses));
        Assert.Equal(header.Date, eerp.Date);
        Assert.Equal(header.Time, eerp.Time);
    }

    [Fact]
    public async Task TheLowerProtocolLevelOfTheTwoIsUsed()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        client.Enqueue("MIXED", "content"u8.ToArray(), ServerCode);

        // We offer OFTP 2.0, the responder only revision 1.3.
        var (clientError, serverError) = await RunAsync(client, server,
            level: ProtocolLevels.Oftp2, responderLevel: ProtocolLevels.Oftp13);

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal("content"u8.ToArray(), server.ReceivedFiles["MIXED"]);
        Assert.Equal(6, server.ReceivedHeaders["MIXED"].Date.Length);
    }

    [Fact]
    public async Task SecuredFileIsNotSentWithAnOldProtocolLevel()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        client.Enqueue("SECURED", "content"u8.ToArray(), ServerCode,
            securityLevel: SecurityLevels.EncryptedAndSigned,
            cipherSuite: CipherSuites.Aes256Sha256,
            enveloping: FileEnvelopingFormats.Cms);

        var (clientError, serverError) = await RunAsync(client, server, level: ProtocolLevels.Oftp14);

        Assert.Equal(ReasonCodes.ModeOrCapabilitiesIncompatible,
            Assert.IsType<OftpProtocolException>(clientError).ReasonCode);
        Assert.Empty(server.ReceivedFiles);
        Assert.NotNull(serverError);
    }

    /// <summary>Records of a fixed file are stored one after another, of a variable file with a 2 octet length.</summary>
    private static byte[] FixedRecords(int count, int length) =>
        Enumerable.Range(0, count).SelectMany(i => Enumerable.Repeat((byte)('A' + i % 26), length)).ToArray();

    private static byte[] VariableRecords(params int[] lengths)
    {
        var file = new List<byte>();
        for (var i = 0; i < lengths.Length; i++)
        {
            file.Add((byte)(lengths[i] >> 8));
            file.Add((byte)lengths[i]);
            file.AddRange(Enumerable.Repeat((byte)('a' + i % 26), lengths[i]));
        }

        return file.ToArray();
    }

    [Fact]
    public async Task FixedFormatFileIsTransferredAsRecords()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        // 40 records of 128 octets: with a 256 octet buffer a record spans several data exchange buffers.
        var content = FixedRecords(40, 128);
        client.Enqueue("F128", content, ServerCode, format: FileFormats.Fixed, maxRecordSize: 128);

        var (clientError, serverError) = await RunAsync(client, server, bufferSize: 256, credit: 4);

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal(content, server.ReceivedFiles["F128"]);

        var header = server.ReceivedHeaders["F128"];
        Assert.Equal(FileFormats.Fixed, header.Format);
        Assert.Equal(128, header.MaxRecordSize);
    }

    [Fact]
    public async Task VariableFormatFileIsTransferredAsRecords()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        // Records of different lengths, including an empty one and one of the maximum length.
        var content = VariableRecords(44, 1, 0, 17, 44, 3);
        client.Enqueue("V44", content, ServerCode, format: FileFormats.Variable, maxRecordSize: 44);

        var (clientError, serverError) = await RunAsync(client, server, bufferSize: 128, credit: 2);

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal(content, server.ReceivedFiles["V44"]);
        Assert.Equal(FileFormats.Variable, server.ReceivedHeaders["V44"].Format);
        Assert.Equal(44, server.ReceivedHeaders["V44"].MaxRecordSize);
    }

    [Fact]
    public async Task TextFileIsTransferredAsASingleRecord()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        // A text file keeps its line separators in the data, it has no record structure on the wire.
        var content = System.Text.Encoding.ASCII.GetBytes(
            string.Join("\r\n", Enumerable.Range(0, 200).Select(i => $"Line {i} of the test file")));
        client.Enqueue("TEXT", content, ServerCode, format: FileFormats.Text);

        var (clientError, serverError) = await RunAsync(client, server, bufferSize: 512, credit: 3);

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal(content, server.ReceivedFiles["TEXT"]);
        Assert.Equal(FileFormats.Text, server.ReceivedHeaders["TEXT"].Format);
        // Text and unstructured files carry no record length.
        Assert.Equal(0, server.ReceivedHeaders["TEXT"].MaxRecordSize);
    }

    [Fact]
    public async Task RecordFilesAreTransferredWithBufferCompression()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        var content = FixedRecords(20, 128);
        client.Enqueue("PADDED", content, ServerCode, format: FileFormats.Fixed, maxRecordSize: 128);

        var (clientError, serverError) = await RunAsync(client, server, bufferSize: 256, credit: 4,
            bufferCompression: true);

        Assert.Null(clientError);
        Assert.Null(serverError);
        Assert.Equal(content, server.ReceivedFiles["PADDED"]);
    }

    [Fact]
    public async Task UndeliverableFileIsAnsweredWithNerp()
    {
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        client.Enqueue("LOST", "content"u8.ToArray(), ServerCode);
        server.NotDeliverableDatasetNames.Add("LOST");

        var (clientError, serverError) = await RunAsync(client, server);

        Assert.Null(clientError);
        Assert.Null(serverError);

        var nerp = Assert.IsType<NERP>(Assert.Single(client.ReceivedEndResponses));
        Assert.Equal("LOST", nerp.DatasetName);
        Assert.Equal(ClientCode, nerp.Destination);
        Assert.Equal(ServerCode, nerp.Originator);
        // The node that could not deliver the file is the creator of the response.
        Assert.Equal(ServerCode, nerp.Creator);
        Assert.Equal(AnswerReasonCodes.InvalidDestination, nerp.ReasonCode);
        Assert.Equal("The final destination does not accept the file.", nerp.ReasonText);
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
    public async Task ConnectionOverTheSessionLimitIsRefusedWithEsid()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refused = 0;

        await using var listener = new OftpListener(new IPEndPoint(IPAddress.Loopback, 0), null,
            (_, _, ct) => release.Task.WaitAsync(ct), NullLogger.Instance, maxSessions: 1);
        listener.SessionLimitReached += _ => Interlocked.Increment(ref refused);
        listener.Start();

        await using var first = await OftpConnector.ConnectAsync("localhost", listener.LocalEndPoint.Port, null, cts.Token);
        while (listener.ActiveSessions == 0)
            await Task.Delay(10, cts.Token);

        // The second partner is told to try later instead of getting SSRM.
        await using (var second = await OftpConnector.ConnectAsync("localhost", listener.LocalEndPoint.Port, null, cts.Token))
        {
            var esid = Assert.IsType<ESID>(OftpCommand.Decode(await second.ReadAsync(cts.Token)));
            Assert.Equal(ReasonCodes.ResourcesNotAvailable, esid.ReasonCode);
        }

        Assert.Equal(1, refused);
        Assert.Equal(1, listener.ActiveSessions);

        // A slot freed by the first session is available again.
        release.SetResult();
        while (listener.ActiveSessions > 0)
            await Task.Delay(10, cts.Token);
        Assert.Equal(1, refused);
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

    [Fact]
    public async Task ExpiredServerCertificateIsRejectedAlthoughItIsPinned()
    {
        using var certificate = CreateSelfSignedCertificate(expired: true);
        var client = CreateClientHandler();
        var server = CreateServerHandler();
        client.Enqueue("SECURE", "over tls"u8.ToArray(), ServerCode);

        var (clientError, _) = await RunAsync(client, server,
            serverTls: new OftpTlsOptions { LocalCertificate = certificate },
            clientTls: new OftpTlsOptions
            {
                TrustedCertificates = [X509CertificateLoader.LoadCertificate(certificate.RawData)],
            });

        Assert.IsType<AuthenticationException>(clientError, exactMatch: false);
        Assert.Empty(server.ReceivedFiles);
    }

    [Fact]
    public async Task ExpiredClientCertificateIsRejectedAlthoughItIsPinned()
    {
        using var serverCertificate = CreateSelfSignedCertificate();
        using var clientCertificate = CreateSelfSignedCertificate(expired: true);
        using var cts = new CancellationTokenSource(TestTimeout);
        var accepted = false;

        await using var listener = new OftpListener(new IPEndPoint(IPAddress.Loopback, 0),
            new OftpTlsOptions
            {
                LocalCertificate = serverCertificate,
                RequireClientCertificate = true,
                TrustedCertificates = [X509CertificateLoader.LoadCertificate(clientCertificate.RawData)],
            },
            (_, _, _) =>
            {
                accepted = true;
                return Task.CompletedTask;
            }, NullLogger.Instance);
        listener.Start();

        try
        {
            await using var transport = await OftpConnector.ConnectAsync("localhost", listener.LocalEndPoint.Port,
                new OftpTlsOptions
                {
                    LocalCertificate = clientCertificate,
                    TrustedCertificates = [X509CertificateLoader.LoadCertificate(serverCertificate.RawData)],
                }, cts.Token);

            // The client may finish its side of the handshake; the server drops the connection right after.
            await transport.ReadAsync(cts.Token);
        }
        catch (Exception)
        {
            // Expected: the server refused the expired certificate.
        }

        Assert.False(accepted, "The session was accepted although the client certificate had expired.");
    }

    private static X509Certificate2 CreateSelfSignedCertificate(bool expired = false)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());

        var (from, to) = expired
            ? (DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now.AddDays(-1))
            : (DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));

        using var ephemeral = request.CreateSelfSigned(from, to);
        // Round trip through PKCS#12 so the private key is usable by SslStream on all platforms.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }
}
