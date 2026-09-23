namespace Oftp4Net.Services.Pdx;

/// <summary>
/// Which OFTP2 Communication Setups received over OFTP are applied without the administrator (Odette OP08 part 3,
/// "Security": a datasheet without a valid signature should be confirmed by a user).
/// </summary>
public enum PdxAutoApply
{
    /// <summary>Every datasheet waits for approval.</summary>
    Never = 0,

    /// <summary>Datasheets signed with the partner's certificate are applied, the others wait for approval.</summary>
    SignedOnly = 1,

    /// <summary>Every compatible datasheet is applied (e.g. for the Odette interoperability tests).</summary>
    Always = 2,
}
