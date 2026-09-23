using Havit.Data.Patterns.UnitOfWorks;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Import;

/// <summary>Result of an import run.</summary>
public sealed record Os4xImportResult(int Partners, int Identities, IReadOnlyList<string> Errors);

/// <summary>
/// Reads the partner table of an OS4X installation (MariaDB / MySQL) and creates the partners that are not here
/// yet. Existing partners are never changed: they are only reported as already present.
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

        var known = (await partners.GetAllAsync(cancellationToken))
            .Select(p => p.SSID.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<Os4xPartnerCandidate>();
        foreach (var row in rows)
        {
            var candidate = Os4xPartnerMapper.Map(row);
            if (candidate.Partner is not null && known.Contains(candidate.Partner.SSID))
                candidate.AlreadyExists = true;

            candidate.Selected = candidate.CanImport;
            candidates.Add(candidate);
        }

        return candidates;
    }

    /// <summary>Creates the selected partners together with the identities they are used with.</summary>
    public async Task<Os4xImportResult> ImportAsync(IEnumerable<Os4xPartnerCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var identityCache = new Dictionary<string, Identity>(StringComparer.OrdinalIgnoreCase);
        var importedPartners = 0;
        var createdIdentities = 0;

        foreach (var candidate in candidates.Where(c => c.CanImport))
        {
            var partner = candidate.Partner!;

            // The import only adds, so a partner that appeared in the meantime is left alone.
            if (await partners.FindBySsidAsync(partner.SSID, cancellationToken) is not null)
            {
                errors.Add($"{partner.Name}: a partner with the code {partner.SSID} already exists.");
                continue;
            }

            if (!string.IsNullOrEmpty(candidate.IdentitySsid))
                createdIdentities += await GetIdentityAsync(candidate, identityCache, cancellationToken);

            unitOfWork.AddForInsert(partner);
            importedPartners++;
        }

        if (importedPartners > 0 || createdIdentities > 0)
            await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Imported {Partners} partner(s) and {Identities} identity(ies) from OS4X",
            importedPartners, createdIdentities);

        return new Os4xImportResult(importedPartners, createdIdentities, errors);
    }

    /// <summary>Returns 1 when the identity of the candidate had to be created, 0 when it already exists.</summary>
    private async Task<int> GetIdentityAsync(Os4xPartnerCandidate candidate, Dictionary<string, Identity> cache,
        CancellationToken cancellationToken)
    {
        if (cache.ContainsKey(candidate.IdentitySsid))
            return 0;

        var existing = (await identities.GetAllAsync(cancellationToken))
            .FirstOrDefault(i => string.Equals(i.SSID.Trim(), candidate.IdentitySsid, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            cache.Add(candidate.IdentitySsid, existing);
            return 0;
        }

        var identity = new Identity
        {
            Name = candidate.IdentitySsid,
            Description = $"Imported from OS4X with partner {candidate.Partner!.Name}",
            SSID = candidate.IdentitySsid,
            SFID = candidate.IdentitySfid,
            Password = candidate.IdentityPassword,
        };

        unitOfWork.AddForInsert(identity);
        cache.Add(candidate.IdentitySsid, identity);
        return 1;
    }

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
                    address, port, port_tls, use_tls, oftp_version, oftp2_cipher_suite, oftpv2_sign, oftpv2_encrypt,
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
                UseTls = Number(reader, "use_tls") != 0,
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
