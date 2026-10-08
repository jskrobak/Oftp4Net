using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;

namespace Oftp4Net.Services.Tests;

public class SubStationEditorTests
{
    private static Partner Partner() => new()
    {
        Name = "Clearing",
        SFID = "O0013CLEARING",
        SubStations =
        [
            new PartnerSubStation { Name = "Plant", SFID = "O0013PLANT" },
            new PartnerSubStation { Name = "Customer", SFID = "O0013CUSTOMER" },
        ],
        Certificates =
        [
            new CertificateAssignment { Sfid = "O0013PLANT", Usage = CertificateUsage.FileSignature, CertificateId = 1 },
            new CertificateAssignment { Usage = CertificateUsage.FileEncryption, CertificateId = 2 },
        ],
    };

    [Theory]
    [InlineData("", "O0013NEW", "name")]
    [InlineData("New", " ", "SFID")]
    [InlineData("New", "O0013ABCDEFGHIJKLMNOPQRSTU", "longer")]
    [InlineData("New", "o0013clearing", "of the partner")]
    [InlineData("New", " O0013CUSTOMER ", "already")]
    public void Validate_RefusesNewStation(string name, string sfid, string reason)
    {
        var error = SubStationEditor.Validate(Partner(), null, new PartnerSubStation { Name = name, SFID = sfid });

        Assert.NotNull(error);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void Validate_AcceptsStationKeepingItsSfid()
    {
        Assert.Null(SubStationEditor.Validate(Partner(), "O0013PLANT", new PartnerSubStation { Name = "Plant 2", SFID = "O0013PLANT" }));
    }

    [Fact]
    public void Validate_RefusesSfidOfAnotherStation()
    {
        Assert.NotNull(SubStationEditor.Validate(Partner(), "O0013PLANT", new PartnerSubStation { Name = "Plant", SFID = "O0013CUSTOMER" }));
    }

    [Fact]
    public void Save_AddsNewStationAsNewList()
    {
        var partner = Partner();
        var before = partner.SubStations;

        SubStationEditor.Save(partner, null, new PartnerSubStation { Name = " New ", SFID = " O0013NEW " });

        Assert.NotSame(before, partner.SubStations);
        Assert.Equal(["O0013PLANT", "O0013CUSTOMER", "O0013NEW"], partner.SubStations.Select(s => s.SFID));
        Assert.Equal("New", partner.SubStations[2].Name);
    }

    [Fact]
    public void Save_ReplacesStationInPlaceAndMovesItsCertificates()
    {
        var partner = Partner();

        SubStationEditor.Save(partner, "O0013PLANT", new PartnerSubStation { Name = "Plant", SFID = "O0013PLANT2", SignFiles = false });

        Assert.Equal(["O0013PLANT2", "O0013CUSTOMER"], partner.SubStations.Select(s => s.SFID));
        Assert.False(partner.SubStations[0].SignFiles);
        Assert.Equal(["O0013PLANT2", null], partner.Certificates.Select(c => c.Sfid));
        Assert.Equal(1, partner.Certificates[0].CertificateId);
    }

    [Fact]
    public void Remove_DropsStationAndItsCertificates()
    {
        var partner = Partner();

        SubStationEditor.Remove(partner, "o0013plant");

        Assert.Equal(["O0013CUSTOMER"], partner.SubStations.Select(s => s.SFID));
        Assert.Equal([null], partner.Certificates.Select(c => c.Sfid));
    }

    [Fact]
    public void Copy_DoesNotShareContacts()
    {
        var station = new PartnerSubStation { Name = "Plant", SFID = "O0013PLANT", Contacts = [new PartnerContact { Name = "A", Emails = ["a@x"] }] };

        var copy = SubStationEditor.Copy(station);
        copy.Contacts[0].Emails.Add("b@x");
        copy.Contacts.Add(new PartnerContact());

        Assert.Single(station.Contacts);
        Assert.Equal(["a@x"], station.Contacts[0].Emails);
    }
}
