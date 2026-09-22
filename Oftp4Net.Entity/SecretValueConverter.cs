using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Oftp4Net.Entity;

/// <summary>
/// Encrypts secrets (passwords) stored in the database with ASP.NET Core Data Protection.
/// Values written before encryption was introduced are read as plain text and encrypted on the next save.
/// </summary>
public class SecretValueConverter(IDataProtector protector) : ValueConverter<string, string>(
    value => Protect(protector, value),
    value => Unprotect(protector, value))
{
    public const string Prefix = "enc:";
    public const string PurposeName = "Oftp4Net.Entity.Secrets";

    private static string Protect(IDataProtector protector, string value) =>
        value.Length == 0 ? value : Prefix + protector.Protect(value);

    private static string Unprotect(IDataProtector protector, string value) =>
        value.StartsWith(Prefix, StringComparison.Ordinal) ? protector.Unprotect(value[Prefix.Length..]) : value;
}
