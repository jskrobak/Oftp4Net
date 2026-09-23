namespace Oftp4Net.Domain;

/// <summary>
/// How a station stands to a security feature, as declared in the OFTP2 Communication Setup (PDX, Odette OP08
/// part 3). The effective setting of a connection results from comparing the declarations of both stations.
/// </summary>
public enum SecurityUsage
{
    /// <summary>The station cannot or does not want to use the feature.</summary>
    Forbidden = 0,

    /// <summary>Supported, but used only when the partner asks for it.</summary>
    Optional = 1,

    /// <summary>Supported and used unless the partner does not support it.</summary>
    Preferred = 2,

    /// <summary>Every exchange with the station must use the feature.</summary>
    Required = 3,
}
