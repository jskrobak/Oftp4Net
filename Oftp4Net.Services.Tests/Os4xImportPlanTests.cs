using Oftp4Net.Domain;
using Oftp4Net.Services.Import;

namespace Oftp4Net.Services.Tests;

/// <summary>
/// What the import does with a whole OS4X partner table: names split at "__", sub-stations under their partner,
/// one partner per code and one identity per SSID and SFID.
/// </summary>
public class Os4xImportPlanTests
{
    private const string Us = "O01770000000000X0HU000000";

    private static Os4xPartnerRow Partner(long idx, string name, string ssid, string sfid = "", string mySfid = Us,
        string myPassword = "ARTIPA01", bool active = true) => new()
    {
        Idx = idx, ShortName = name, HisSsid = ssid, HisSfid = sfid.Length > 0 ? sfid : ssid, HisPassword = "THEIRS",
        MySsid = Us, MySfid = mySfid, MyPassword = myPassword, Address = "oftp.example.com", Port = 3305, PortTls = 6619,
        AddressType = Os4xAddressTypes.Tls, OftpVersion = 2, Active = active,
    };

    private static Os4xPartnerRow SubStation(long idx, string name, long parent, string sfid, bool active = true) => new()
    {
        Idx = idx, ShortName = name, HisSsid = "", HisSfid = sfid, MySsid = "", MySfid = Us, SubStationOf = parent,
        OftpVersion = 2, Active = active,
    };

    private static List<Os4xPartnerCandidate> Plan(params Os4xPartnerRow[] rows) =>
        Os4xPartnerImporter.Plan(rows, [], []);

    private static Os4xPartnerCandidate Row(List<Os4xPartnerCandidate> plan, string name) =>
        plan.Single(c => c.Name == name);

    [Fact]
    public void SubStationBelongsToItsPartner()
    {
        var plan = Plan(
            Partner(1, "ARTIPA__MAHLE", "O0013000029MAHLE"),
            SubStation(2, "ARTIPA__BEHR-OSTRAVA", 1, "O0013000015BEHROV"));

        var station = Row(plan, "ARTIPA__BEHR-OSTRAVA");
        Assert.True(station.CanImport);
        Assert.True(station.Selected);
        Assert.Equal("BEHR-OSTRAVA", station.SubStation!.Name);
        Assert.Equal("O0013000015BEHROV", station.SubStation.SFID);
        Assert.Equal("sub-station BEHR-OSTRAVA of MAHLE", station.Target);
    }

    [Fact]
    public void SubStationWithTheSfidOfItsPartnerIsLeftOut()
    {
        var plan = Plan(
            Partner(1, "ARTIPA__MAHLE", "O0013000029MAHLE"),
            SubStation(2, "ARTIPA__MAHLE-MONTBLANC", 1, "O0013000029MAHLE"));

        var station = Row(plan, "ARTIPA__MAHLE-MONTBLANC");
        Assert.False(station.CanImport);
        Assert.Contains(station.Notes, n => n.Contains("SFID of its partner") && n.Contains("Oftp:Oftp4Net:Partners"));
    }

    [Fact]
    public void SameSubStationInSeveralRowsIsTakenOnceThePreferablyActiveOne()
    {
        var plan = Plan(
            Partner(1, "ARTIPA__AIMTEC", "O0942CZ2520181630122AIM"),
            SubStation(2, "ARTIPA__THERMOPLASTIC", 1, "AIM01P510503605THE"),
            SubStation(3, "ARTIPA__THERMOPLASTIK", 1, "AIM01P510503605THE", active: false));

        Assert.True(Row(plan, "ARTIPA__THERMOPLASTIC").CanImport);
        Assert.False(Row(plan, "ARTIPA__THERMOPLASTIK").CanImport);
    }

    [Fact]
    public void SubStationWithoutSfidOrPartnerCannotBeImported()
    {
        var plan = Plan(
            Partner(1, "ARTIPA__BENSELER", "O0013002474BENSELER"),
            SubStation(2, "ARTIPA__BENSELER-110", 1, ""),
            SubStation(3, "ARTIPA__ORPHAN", 99, "O0013ORPHAN"));

        Assert.False(Row(plan, "ARTIPA__BENSELER-110").CanImport);
        Assert.Contains(Row(plan, "ARTIPA__BENSELER-110").Notes, n => n.Contains("no SFID"));
        Assert.False(Row(plan, "ARTIPA__ORPHAN").CanImport);
    }

    [Fact]
    public void SubStationOfAPartnerThatExistsAlreadyIsNotImported()
    {
        var plan = Os4xPartnerImporter.Plan(
            [Partner(1, "ARTIPA__MAHLE", "O0013000029MAHLE"), SubStation(2, "ARTIPA__BEHR-OSTRAVA", 1, "O0013000015BEHROV")],
            [new Partner { Name = "MAHLE", SSID = "O0013000029MAHLE" }], []);

        var station = Row(plan, "ARTIPA__BEHR-OSTRAVA");
        Assert.False(station.CanImport);
        Assert.Contains(station.Notes, n => n.Contains("by hand"));
    }

    [Fact]
    public void PartnerInSeveralRowsIsSelectedOnce()
    {
        // ARTIPA__VW and LETOPLAST__VW-AUDI are one partner known by two codes of ours.
        var plan = Plan(
            Partner(1, "ARTIPA__VW", "O0013000001VW      KOI"),
            Partner(2, "LETOPLAST__VW-AUDI", "O0013000001VW      KOI", mySfid: "O094248591726679LET"),
            SubStation(3, "ARTIPA__VW-AUDI", 1, "O0013000001VW      KEY"));

        Assert.True(Row(plan, "ARTIPA__VW").Selected);
        Assert.False(Row(plan, "LETOPLAST__VW-AUDI").Selected);
        Assert.True(Row(plan, "LETOPLAST__VW-AUDI").CanImport);
        Assert.Contains(Row(plan, "LETOPLAST__VW-AUDI").Notes, n => n.Contains("several rows"));
        Assert.Equal("sub-station VW-AUDI of VW", Row(plan, "ARTIPA__VW-AUDI").Target);
    }

    [Fact]
    public void IdentityIsNamedAfterThePartBeforeTheSeparatorPerSsidAndSfid()
    {
        var plan = Plan(
            Partner(1, "ARTIPA__VW", "VW"),
            Partner(2, "ARTIPA__MAHLE", "MAHLE"),
            Partner(3, "ARTIPA__HAJDIK", "HAJDIK", mySfid: "HAJDIK-MOULDING"),
            Partner(4, "LETOPLAST__SKODA", "SKODA", mySfid: "O094248591726679LET"));

        Assert.Equal("ARTIPA", Row(plan, "ARTIPA__VW").IdentityName);
        Assert.Equal("ARTIPA", Row(plan, "ARTIPA__MAHLE").IdentityName);
        Assert.Equal("LETOPLAST", Row(plan, "LETOPLAST__SKODA").IdentityName);

        // The same name with another SFID of ours: the less used one gets its SFID added.
        var other = Row(plan, "ARTIPA__HAJDIK");
        Assert.Equal("ARTIPA HAJDIK-MOULDING", other.IdentityName);
        Assert.Contains(other.Notes, n => n.Contains("named ARTIPA HAJDIK-MOULDING") && n.Contains("Oftp:Oftp4Net:Partners"));
        Assert.DoesNotContain(Row(plan, "ARTIPA__VW").Notes, n => n.Contains("is named"));
    }

    [Fact]
    public void ExistingIdentityKeepsItsName()
    {
        var plan = Os4xPartnerImporter.Plan([Partner(1, "ARTIPA__VW", "VW")], [],
            [new Identity { Name = Us, SSID = Us, SFID = Us, Password = "ARTIPA01" }]);

        var row = Row(plan, "ARTIPA__VW");
        Assert.Equal(Us, row.IdentityName);
        Assert.Contains(row.Notes, n => n.Contains($"is named {Us} here"));
    }

    [Fact]
    public void DifferentPasswordsOfOneIdentityAreNoted()
    {
        var plan = Plan(
            Partner(1, "ARTIPA__MAHLE", "MAHLE"),
            Partner(2, "ARTIPA__VW", "VW", myPassword: "KOI00001"));

        Assert.Contains(Row(plan, "ARTIPA__VW").Notes, n => n.Contains("another password in row ARTIPA__MAHLE"));
    }
}

/// <summary>The certificate of a partner for file security, taken from OS4X.</summary>
public class Os4xPartnerCertificateTests
{
    private static readonly DateTime Now = new(2026, 10, 5);

    private static string Pem(string name, DateTime notAfter)
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={name}", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(notAfter.AddYears(-2), notAfter);
        return certificate.ExportCertificatePem();
    }

    private static Os4xPartnerRow Row(long idx, string name, string ssid, params string[] certificates) => new()
    {
        Idx = idx, ShortName = name, HisSsid = ssid, HisSfid = ssid, MySsid = "US", MySfid = "US", MyPassword = "OURS",
        Address = "oftp.example.com", Port = 3305, PortTls = 6619, AddressType = Os4xAddressTypes.Tls, OftpVersion = 2,
        Encrypt = true, Active = true, Certificates = certificates,
    };

    [Fact]
    public void ValidCertificateBecomesTheCertificateOfThePartner()
    {
        var candidate = Os4xPartnerMapper.Map(Row(1, "ARTIPA__MUERDTER", "MUERDTER", Pem("oftp2.muerdter.de", Now.AddYears(3))), Now);

        Assert.Equal("oftp2.muerdter.de", candidate.PartnerCertificate!.Name);
        Assert.False(candidate.PartnerCertificate.HasPrivateKey);
        Assert.DoesNotContain(candidate.Notes, n => n.Contains("assign it after the import"));
        Assert.Contains("certificate until", candidate.Security);
    }

    [Fact]
    public void OfSeveralTheOneValidLongestIsTaken()
    {
        var candidate = Os4xPartnerMapper.Map(Row(1, "ARTIPA__IABA", "IABA",
            Pem("old.ideal-automotive.com", Now.AddYears(1)), Pem("new.ideal-automotive.com", Now.AddYears(3))), Now);

        Assert.Equal("new.ideal-automotive.com", candidate.PartnerCertificate!.Name);
    }

    [Fact]
    public void ExpiredCertificateIsNotTaken()
    {
        var candidate = Os4xPartnerMapper.Map(Row(1, "ARTIPA__MB-TOOL", "MBTOOL", Pem("webedi.mbtool.cz", Now.AddDays(-30))), Now);

        Assert.Null(candidate.PartnerCertificate);
        Assert.Contains(candidate.Notes, n => n.Contains("expired"));
        // The partner encrypts files, so the certificate is missing.
        Assert.Contains(candidate.Notes, n => n.Contains("assign it after the import"));
    }

    [Fact]
    public void PartnerInSeveralRowsTakesTheCertificateOfAnotherRow()
    {
        // In OS4X the certificate of VW is in the row LETOPLAST__VW-AUDI only.
        var plan = Os4xPartnerImporter.Plan(
            [Row(1, "ARTIPA__VW", "VWKOI"), Row(2, "LETOPLAST__VW-AUDI", "VWKOI", Pem("oftpv2.volkswagen.de", Now.AddYears(2)))],
            [], [], Now);

        var vw = plan.Single(c => c.Name == "ARTIPA__VW");
        Assert.True(vw.Selected);
        Assert.Equal("oftpv2.volkswagen.de", vw.PartnerCertificate!.Name);
        Assert.Contains(vw.Notes, n => n.Contains("taken from row LETOPLAST__VW-AUDI"));
    }
}
