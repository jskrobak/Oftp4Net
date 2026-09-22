using System.Security.Cryptography;

namespace Oftp4Net.Core.Protocol;

/// <summary>Constants of secure authentication (RFC 5024, sections 4.2.3 and 5.3.17).</summary>
public static class SecureAuthentication
{
    /// <summary>Length of the random challenge in octets (AURPRSP is exactly this long).</summary>
    public const int ChallengeLength = 20;

    /// <summary>Creates the random challenge, generated anew for every AUCH.</summary>
    public static byte[] CreateChallenge() => RandomNumberGenerator.GetBytes(ChallengeLength);
}
