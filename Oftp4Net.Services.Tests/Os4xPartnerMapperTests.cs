using Oftp4Net.Core.Protocol;
using Oftp4Net.Services.Import;

namespace Oftp4Net.Services.Tests;

public class Os4xPartnerMapperTests
{
    private static Os4xPartnerRow Row(Action<Os4xPartnerRowBuilder>? configure = null)
    {
        var builder = new Os4xPartnerRowBuilder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    private sealed class Os4xPartnerRowBuilder
    {
        public string ShortName = "PARTNER";
        public string LongName = "A partner";
        public string HisSsid = "O0013000000PARTNER";
        public string HisSfid = "O0013000000PARTNERF";
        public string HisPassword = "SECRET";
        public string MySsid = "O0013000000US";
        public string MySfid = "O0013000000USF";
        public string MyPassword = "OURS";
        public string Address = "partner.example.com";
        public int Port = 3305;
        public int PortTls = 6619;
        public bool UseTls = true;
        public double OftpVersion = 2;
        public int CipherSuite;
        public bool Sign;
        public bool Encrypt;
        public int CompressionLevel;
        public bool SecureAuthentication;
        public bool RequestSignedEerp;
        public bool Active = true;

        public Os4xPartnerRow Build() => new()
        {
            Idx = 1,
            ShortName = ShortName,
            LongName = LongName,
            HisSsid = HisSsid,
            HisSfid = HisSfid,
            HisPassword = HisPassword,
            MySsid = MySsid,
            MySfid = MySfid,
            MyPassword = MyPassword,
            Address = Address,
            Port = Port,
            PortTls = PortTls,
            UseTls = UseTls,
            OftpVersion = OftpVersion,
            CipherSuite = CipherSuite,
            Sign = Sign,
            Encrypt = Encrypt,
            CompressionLevel = CompressionLevel,
            SecureAuthentication = SecureAuthentication,
            RequestSignedEerp = RequestSignedEerp,
            Active = Active,
        };
    }

    [Fact]
    public void PartnerAndIdentityAreTakenFromTheSameRow()
    {
        var candidate = Os4xPartnerMapper.Map(Row());

        var partner = Assert.IsType<Oftp4Net.Domain.Partner>(candidate.Partner);
        Assert.Equal("PARTNER", partner.Name);
        Assert.Equal("A partner", partner.Description);
        Assert.Equal("O0013000000PARTNER", partner.SSID);
        Assert.Equal("O0013000000PARTNERF", partner.SFID);
        Assert.Equal("SECRET", partner.Password);
        Assert.Equal("partner.example.com", partner.Host);
        // The TLS port is used when the partner is configured for TLS.
        Assert.Equal(6619, partner.Port);
        Assert.True(partner.UseTls);
        Assert.Equal("O0013000000US", candidate.IdentitySsid);
        Assert.Equal("O0013000000USF", candidate.IdentitySfid);
        Assert.Equal("OURS", candidate.IdentityPassword);
        Assert.True(candidate.CanImport);
    }

    [Fact]
    public void PlainPortIsUsedWithoutTls()
    {
        var candidate = Os4xPartnerMapper.Map(Row(r => r.UseTls = false));

        Assert.Equal(3305, candidate.Partner!.Port);
        Assert.False(candidate.Partner.UseTls);
    }

    [Theory]
    [InlineData(1, CipherSuites.TripleDesSha1)]
    [InlineData(2, CipherSuites.Aes256Sha1)]
    [InlineData(4, CipherSuites.Aes256Sha256)]
    [InlineData(6, CipherSuites.Aes256Sha512)]
    // Not configured or unknown: the suite every OFTP2 node supports is used.
    [InlineData(0, CipherSuites.Aes256Sha1)]
    [InlineData(9, CipherSuites.Aes256Sha1)]
    public void CipherSuiteNumberBecomesTheSfidCiphCode(int value, string expected)
    {
        var candidate = Os4xPartnerMapper.Map(Row(r => r.CipherSuite = value));

        Assert.Equal(expected, candidate.Partner!.FileCipherSuite);
    }

    [Fact]
    public void FileSecurityFlagsAreTakenOver()
    {
        var candidate = Os4xPartnerMapper.Map(Row(r =>
        {
            r.Sign = true;
            r.Encrypt = true;
            r.CompressionLevel = 6;
            r.SecureAuthentication = true;
            r.RequestSignedEerp = true;
        }));

        var partner = candidate.Partner!;
        Assert.True(partner.SignFiles);
        Assert.True(partner.EncryptFiles);
        Assert.True(partner.CompressFiles);
        Assert.True(partner.SecureAuthentication);
        Assert.True(partner.RequestSignedEndResponse);
        Assert.Contains(candidate.Notes, n => n.Contains("certificate"));
    }

    [Fact]
    public void Oftp1PartnerIsImportedWithTheOlderRelease()
    {
        var candidate = Os4xPartnerMapper.Map(Row(r =>
        {
            r.OftpVersion = 1;
            r.Sign = true;
            r.Encrypt = true;
        }));

        var partner = candidate.Partner!;
        Assert.Equal(Oftp4Net.Core.Protocol.ProtocolLevels.Oftp14, partner.ProtocolLevel);
        // File level security exists only in OFTP 2.0, so it is not taken over.
        Assert.False(partner.SignFiles);
        Assert.False(partner.EncryptFiles);
        Assert.Contains(candidate.Notes, n => n.Contains("OFTP 1"));
    }

    [Fact]
    public void Oftp2PartnerKeepsTheCurrentRelease()
    {
        var candidate = Os4xPartnerMapper.Map(Row());

        Assert.Equal(Oftp4Net.Core.Protocol.ProtocolLevels.Oftp2, candidate.Partner!.ProtocolLevel);
    }

    [Fact]
    public void PartnerWithoutCodeCannotBeImported()
    {
        var candidate = Os4xPartnerMapper.Map(Row(r => r.HisSsid = "  "));

        Assert.Null(candidate.Partner);
        Assert.Contains(candidate.Notes, n => n.Contains("identification code"));
    }

    [Fact]
    public void LongValuesAreShortenedToTheColumnLengths()
    {
        var candidate = Os4xPartnerMapper.Map(Row(r =>
        {
            r.ShortName = new string('N', 120);
            r.MyPassword = "0123456789";
        }));

        Assert.Equal(50, candidate.Partner!.Name.Length);
        Assert.Equal("01234567", candidate.IdentityPassword);
        Assert.Contains(candidate.Notes, n => n.Contains("shortened"));
    }

    [Fact]
    public void MissingNamesAndCodesFallBackToTheOdetteId()
    {
        var candidate = Os4xPartnerMapper.Map(Row(r =>
        {
            r.ShortName = "";
            r.HisSfid = "";
            r.MySfid = "";
        }));

        Assert.Equal("O0013000000PARTNER", candidate.Partner!.Name);
        Assert.Equal("O0013000000PARTNER", candidate.Partner.SFID);
        Assert.Equal("O0013000000US", candidate.IdentitySfid);
    }

    [Fact]
    public void InactivePartnerIsImportedWithANote()
    {
        var candidate = Os4xPartnerMapper.Map(Row(r => r.Active = false));

        Assert.True(candidate.CanImport);
        Assert.Contains(candidate.Notes, n => n.Contains("inactive"));
    }
}
