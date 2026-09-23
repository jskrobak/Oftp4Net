using Oftp4Net.Domain;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Pdx;

/// <summary>Result of comparing the usage of a security feature declared by two stations.</summary>
public enum NegotiationResult
{
    Off,
    On,

    /// <summary>One station requires the feature, the other one forbids it: the settings are incompatible.</summary>
    Conflict,
}

/// <summary>
/// Compares the security settings of two datasheets as described in Odette OP08 part 3 ("Interpreting the security
/// settings"): the inbound setting of one station is compared with the outbound setting of the other one.
/// </summary>
public static class PdxNegotiator
{
    /// <summary>
    /// The table of OP08 part 3: forbidden turns the feature off (a conflict with required), required turns it on,
    /// optional with optional stays off, everything else is on. The table is symmetric.
    /// </summary>
    public static NegotiationResult Resolve(SecurityUsage own, SecurityUsage theirs) => (own, theirs) switch
    {
        (SecurityUsage.Forbidden, SecurityUsage.Required) or (SecurityUsage.Required, SecurityUsage.Forbidden) => NegotiationResult.Conflict,
        (SecurityUsage.Forbidden, _) or (_, SecurityUsage.Forbidden) => NegotiationResult.Off,
        (SecurityUsage.Optional, SecurityUsage.Optional) => NegotiationResult.Off,
        _ => NegotiationResult.On,
    };

    /// <summary>
    /// The cipher suite used with the partner: its primary suite when we accept it, otherwise the first of its
    /// alternates we accept. <c>null</c> when there is no suite both sides accept.
    /// </summary>
    public static CipherSuite? ResolveCipherSuite(StationProfile own, PdxCipherSetting theirs)
    {
        var accepted = AcceptedCipherSuites(own);
        return theirs.All.Select(CipherSuite.Get).FirstOrDefault(s => s is not null && accepted.Contains(s.Code));
    }

    /// <summary>Suites of our profile the platform supports; all supported ones when the profile lists no alternates.</summary>
    public static HashSet<string> AcceptedCipherSuites(StationProfile own)
    {
        var supported = CipherSuite.Supported.Select(s => s.Code).ToHashSet();
        if (own.AlternateCipherSuites.Count == 0)
            return supported;

        return own.AlternateCipherSuites.Append(own.PrimaryCipherSuite).Where(supported.Contains).ToHashSet();
    }
}
