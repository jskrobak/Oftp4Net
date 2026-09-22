namespace Oftp4Net.Domain;

/// <summary>Character encoding of the content of a virtual file.</summary>
public enum FileCharacterEncoding
{
    /// <summary>Single byte ANSI / ISO code page, the encoding files are stored in locally.</summary>
    ANSI,

    /// <summary>IBM EBCDIC code page used by mainframe partners.</summary>
    EBCDIC,
}
