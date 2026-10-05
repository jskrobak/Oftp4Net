using Havit.Data.Patterns.UnitOfWorks;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Import;

/// <summary>Result of an import run.</summary>
public sealed record Os4xImportResult(int Partners, int SubStations, int Identities, IReadOnlyList<string> Errors);

/// <summary>
/// Reads the partner table of an OS4X installation (MariaDB / MySQL) and creates the partners that are not here
/// yet, with their sub-stations and the identities they are used with. Existing partners are never changed: they
/// are only reported as already present.
/// </summary>
public class Os4xPartnerImporter(
    IPartnerRepository partners,
    IIdentityRepository identities,
    IUnitOfWork unitOfWork,
    ILogger<Os4xPartnerImporter> logger)
{
    /// <summary>Reads the partners of the OS4X installation and says what the import would do with each of them.</summary>
    public async Task<List<Os4xPartnerCandidate>> LoadAsync(Os4xImportOptions options, CancellationToken cancellationToken = default)
    {
        var rows = await ReadRowsAsync(options, cancellationToken);
        logger.LogInformation("Read {Count} partner(s) from the OS4X database {Database} on {Host}",
            rows.Count, options.Database, options.Host);

        return Plan(rows, await partners.GetAllAsync(cancellationToken), await identities.GetAllAsync(cancellationToken));
    }

    /// <summary>
    /// What the import would do with the rows of OS4X, given the partners and identities that are here already.
    /// </summary>
    public static List<Os4xPartnerCandidate> Plan(IReadOnlyList<Os4xPartnerRow> rows, IEnumerable<Partner> existingPartners,
        IEnumerable<Identity> existingIdentities)
    {
        var known = existingPartners.Select(p => p.SSID.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = rows.Select(Os4xPartnerMapper.Map).ToList();
        var byIdx = candidates.GroupBy(c => c.Source.Idx).ToDictionary(g => g.Key, g => g.First());

        foreach (var candidate in candidates)
        {
            if (candidate.Partner is not null && known.Contains(candidate.Partner.SSID))
                candidate.AlreadyExists = true;
        }

        // OS4X has a row per identity a partner is used with; only one of them can become the partner here.
        foreach (var group in candidates.Where(c => c.Partner is not null && !c.AlreadyExists)
                     .GroupBy(c => c.Ssid, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var rowNames = string.Join(", ", group.Select(c => c.Source.ShortName));
            foreach (var candidate in group)
                candidate.Notes.Add($"The partner code is in several rows ({rowNames}); only one of them can be imported, " +
                                    "ABP routes of the others need an entry in Oftp:Oftp4Net:Partners.");
        }

        LinkSubStations(candidates, byIdx);
        NameIdentities(candidates, existingIdentities);

        foreach (var candidate in candidates)
            candidate.Selected = candidate.CanImport;

        // The first row of a partner code is selected, the others can be chosen instead.
        foreach (var group in candidates.Where(c => c.Partner is not null && c.CanImport)
                     .GroupBy(c => c.Ssid, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in group.Skip(1))
                candidate.Selected = false;
        }

        return candidates;
    }

    /// <summary>
    /// Attaches every sub-station to the row of its partner. A sub-station with the SFID of its partner would take
    /// over the partner's own files, so it is left out; of the same sub-station in several rows only one is taken.
    /// </summary>
    private static void LinkSubStations(List<Os4xPartnerCandidate> candidates, Dictionary<long, Os4xPartnerCandidate> byIdx)
    {
        foreach (var candidate in candidates.Where(c => c.SubStation is not null))
        {
            if (!byIdx.TryGetValue(candidate.Source.SubStationOf, out var parent) || parent.Partner is null)
            {
                candidate.Notes.Add($"The partner of the sub-station (row {candidate.Source.SubStationOf}) cannot be imported.");
                continue;
            }

            candidate.Parent = parent;
            if (parent.AlreadyExists)
                candidate.Notes.Add($"Its partner {parent.Partner.Name} exists here already; add the sub-station there by hand.");
            else if (string.Equals(candidate.SubStation!.SFID, parent.Partner.SFID.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                candidate.Skipped = true;
                candidate.Notes.Add($"The sub-station has the SFID of its partner {parent.Partner.Name}: files go to the partner " +
                                    $"itself; ABP routes of {candidate.Name} need an entry in Oftp:Oftp4Net:Partners.");
            }
        }

        foreach (var group in candidates.Where(c => c.SubStation is not null && c.Parent is not null && !c.Skipped)
                     .GroupBy(c => (c.Parent!.Source.Idx, Sfid: c.SubStation!.SFID.ToUpperInvariant())))
        {
            var kept = group.OrderByDescending(c => c.Source.Active).First();
            foreach (var candidate in group.Where(c => c != kept))
            {
                candidate.Skipped = true;
                candidate.Notes.Add($"The same sub-station (SFID {kept.SubStation!.SFID}) is imported from row {kept.Name}.");
            }
        }
    }

    /// <summary>
    /// Names our identities after the part of the OS4X name before "__". An identity is one SSID with one SFID; when
    /// rows with the same name use several of them, the most used one gets the name and the others get their SFID
    /// added, so that ABP routes of those rows need an entry in Oftp:Oftp4Net:Partners. An identity that is here
    /// already keeps its name.
    /// </summary>
    private static void NameIdentities(List<Os4xPartnerCandidate> candidates, IEnumerable<Identity> existingIdentities)
    {
        var existing = existingIdentities
            .GroupBy(i => IdentityKey(i.SSID, i.SFID))
            .ToDictionary(g => g.Key, g => g.First());
        var withIdentity = candidates.Where(c => c.Partner is not null && !c.AlreadyExists && c.IdentitySsid.Length > 0).ToList();

        // The most used SSID/SFID of a name gets it; a pair keeps the name it got first.
        var names = new Dictionary<string, string>();
        var taken = new HashSet<string>(existing.Values.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in withIdentity
                     .GroupBy(c => (Name: c.IdentityName, Key: IdentityKey(c.IdentitySsid, c.IdentitySfid)))
                     .OrderByDescending(g => g.Count()))
        {
            if (names.ContainsKey(pair.Key.Key))
                continue;

            if (existing.TryGetValue(pair.Key.Key, out var identity))
                names[pair.Key.Key] = identity.Name;
            else
            {
                var sfid = pair.First().IdentitySfid;
                var name = taken.Contains(pair.Key.Name) ? Truncate($"{pair.Key.Name} {sfid}", 50) : pair.Key.Name;
                names[pair.Key.Key] = name;
                taken.Add(name);
            }
        }

        var firstRows = new Dictionary<string, Os4xPartnerCandidate>();
        foreach (var candidate in withIdentity)
        {
            var key = IdentityKey(candidate.IdentitySsid, candidate.IdentitySfid);
            var name = names[key];
            if (!string.Equals(name, candidate.IdentityName, StringComparison.OrdinalIgnoreCase))
                candidate.Notes.Add($"Our identity {candidate.IdentitySsid} / {candidate.IdentitySfid} is named {name} here; ABP " +
                                    $"routes with the identity {candidate.IdentityName} need an entry in Oftp:Oftp4Net:Partners.");
            candidate.IdentityName = name;

            if (existing.TryGetValue(key, out var identity))
                Os4xPartnerMapper.CompareIdentity(candidate, identity, sourceRow: null);
            else if (firstRows.TryGetValue(key, out var first))
                Os4xPartnerMapper.CompareIdentity(candidate,
                    new Identity { SSID = first.IdentitySsid, SFID = first.IdentitySfid, Password = first.IdentityPassword },
                    first.Source.ShortName);
            else
                firstRows.Add(key, candidate);
        }
    }

    /// <summary>Creates the selected partners and sub-stations together with the identities they are used with.</summary>
    public async Task<Os4xImportResult> ImportAsync(IEnumerable<Os4xPartnerCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var selected = candidates.Where(c => c.CanImport).ToList();
        var errors = new List<string>();
        var identityCache = new Dictionary<string, Identity>();
        var imported = new Dictionary<string, Partner>(StringComparer.OrdinalIgnoreCase);
        var importedSubStations = 0;
        var createdIdentities = 0;

        foreach (var candidate in selected.Where(c => c.Partner is not null))
        {
            var partner = candidate.Partner!;

            // The import only adds, so a partner that appeared in the meantime is left alone.
            if (await partners.FindBySsidAsync(partner.SSID, cancellationToken) is not null)
            {
                errors.Add($"{candidate.Name}: a partner with the code {partner.SSID} already exists.");
                continue;
            }

            // OS4X keeps a row per identity the partner is used with; here the partner exists once.
            if (!imported.TryAdd(partner.SSID, partner))
            {
                errors.Add($"{candidate.Name}: the partner with the code {partner.SSID} is already imported from another row.");
                continue;
            }

            // The partner knows us by the code of its row, also when it calls us.
            if (!string.IsNullOrEmpty(candidate.IdentitySsid))
            {
                var (identity, created) = await GetIdentityAsync(candidate, identityCache, cancellationToken);
                partner.InboundIdentity = identity;
                if (created)
                    createdIdentities++;
            }

            unitOfWork.AddForInsert(partner);
        }

        // A sub-station is part of its partner, which has to be imported in the same run.
        foreach (var candidate in selected.Where(c => c.SubStation is not null))
        {
            var parent = candidate.Parent?.Partner;
            if (parent is null || !imported.TryGetValue(parent.SSID, out var partner) || partner != parent)
            {
                errors.Add($"{candidate.Name}: its partner {candidate.Parent?.Name} is not imported.");
                continue;
            }

            var subStation = candidate.SubStation!;
            if (partner.SubStations.Any(s => string.Equals(s.SFID, subStation.SFID, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{candidate.Name}: the partner {partner.Name} has a sub-station with the SFID {subStation.SFID} already.");
                continue;
            }

            partner.SubStations.Add(subStation);
            importedSubStations++;
        }

        if (imported.Count > 0 || createdIdentities > 0)
            await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Imported {Partners} partner(s), {SubStations} sub-station(s) and {Identities} identity(ies) from OS4X",
            imported.Count, importedSubStations, createdIdentities);

        return new Os4xImportResult(imported.Count, importedSubStations, createdIdentities, errors);
    }

    /// <summary>Returns the identity of the candidate and whether it had to be created.</summary>
    private async Task<(Identity Identity, bool Created)> GetIdentityAsync(Os4xPartnerCandidate candidate,
        Dictionary<string, Identity> cache, CancellationToken cancellationToken)
    {
        var key = IdentityKey(candidate.IdentitySsid, candidate.IdentitySfid);
        if (cache.TryGetValue(key, out var cached))
            return (cached, false);

        var existing = (await identities.GetAllAsync(cancellationToken))
            .FirstOrDefault(i => IdentityKey(i.SSID, i.SFID) == key);

        if (existing is not null)
        {
            cache.Add(key, existing);
            return (existing, false);
        }

        var identity = new Identity
        {
            Name = candidate.IdentityName,
            Description = $"Imported from OS4X with partner {candidate.Name}",
            SSID = candidate.IdentitySsid,
            SFID = candidate.IdentitySfid,
            Password = candidate.IdentityPassword,
        };

        unitOfWork.AddForInsert(identity);
        cache.Add(key, identity);
        return (identity, true);
    }

    /// <summary>An identity is one SSID with one SFID.</summary>
    private static string IdentityKey(string ssid, string sfid) =>
        $"{ssid.Trim().ToUpperInvariant()}|{sfid.Trim().ToUpperInvariant()}";

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static async Task<List<Os4xPartnerRow>> ReadRowsAsync(Os4xImportOptions options, CancellationToken cancellationToken)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = options.Host,
            Port = (uint)options.Port,
            Database = options.Database,
            UserID = options.User,
            Password = options.Password,
            ConnectionTimeout = 15,
        };

        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // The table prefix is configurable in OS4X (TABLEPREFIX in os4x.conf), so it cannot be a parameter.
        var table = $"{SafeTableName(options.TablePrefix)}partners";
        await using var command = new MySqlCommand(
            $"""
             SELECT idx, shortname, longname, his_ssid, his_sfid, his_password, my_ssid, my_sfid, my_password,
                    address, addresstype, port, port_tls, substation_reference, oftp_version, oftp2_cipher_suite, oftpv2_sign, oftpv2_encrypt,
                    oftp2_compression_level, oftpv2_sec_auth_req, oftpv2_req_sig_eerp, active
             FROM {table}
             ORDER BY shortname
             """, connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<Os4xPartnerRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new Os4xPartnerRow
            {
                Idx = reader.GetInt64("idx"),
                ShortName = Text(reader, "shortname"),
                LongName = Text(reader, "longname"),
                HisSsid = Text(reader, "his_ssid"),
                HisSfid = Text(reader, "his_sfid"),
                HisPassword = Text(reader, "his_password"),
                MySsid = Text(reader, "my_ssid"),
                MySfid = Text(reader, "my_sfid"),
                MyPassword = Text(reader, "my_password"),
                Address = Text(reader, "address"),
                Port = Number(reader, "port"),
                PortTls = Number(reader, "port_tls"),
                AddressType = Number(reader, "addresstype"),
                SubStationOf = reader.IsDBNull(reader.GetOrdinal("substation_reference")) ? -1 : reader.GetInt64("substation_reference"),
                OftpVersion = reader.IsDBNull(reader.GetOrdinal("oftp_version")) ? 2 : reader.GetDouble("oftp_version"),
                CipherSuite = Number(reader, "oftp2_cipher_suite"),
                Sign = Number(reader, "oftpv2_sign") != 0,
                Encrypt = Number(reader, "oftpv2_encrypt") != 0,
                CompressionLevel = Number(reader, "oftp2_compression_level"),
                SecureAuthentication = Number(reader, "oftpv2_sec_auth_req") != 0,
                RequestSignedEerp = Number(reader, "oftpv2_req_sig_eerp") != 0,
                Active = Number(reader, "active") != 0,
            });
        }

        return rows;
    }

    /// <summary>Only a plain identifier is allowed, the prefix is part of the query text.</summary>
    private static string SafeTableName(string prefix)
    {
        if (prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException($"'{prefix}' is not a valid table prefix.", nameof(prefix));

        return prefix;
    }

    private static string Text(MySqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
    }

    private static int Number(MySqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
    }
}
