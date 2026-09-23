namespace Oftp4Net.Core.Protocol;

/// <summary>End Session (ESID) reason codes, RFC 5024 section 5.3.4.</summary>
public static class ReasonCodes
{
    public const string NormalTermination = "00";
    public const string CommandNotRecognised = "01";
    public const string ProtocolViolation = "02";
    public const string UserCodeNotKnown = "03";
    public const string InvalidPassword = "04";
    public const string LocalSiteEmergencyCloseDown = "05";
    public const string CommandContainedInvalidData = "06";
    public const string ExchangeBufferSizeError = "07";
    public const string ResourcesNotAvailable = "08";
    public const string TimeOut = "09";
    public const string ModeOrCapabilitiesIncompatible = "10";
    public const string InvalidChallengeResponse = "11";
    public const string SecureAuthenticationRequirementsIncompatible = "12";
    public const string UnspecifiedAbortCode = "99";
}

/// <summary>Answer reason codes used by SFNA, EFNA and NERP, RFC 5024 section 5.3.4.</summary>
public static class AnswerReasonCodes
{
    public const string InvalidFilename = "01";
    public const string InvalidDestination = "02";
    public const string InvalidOrigin = "03";
    public const string StorageRecordFormatNotSupported = "04";
    public const string MaximumRecordLengthNotSupported = "05";
    public const string FileSizeTooBig = "06";
    public const string InvalidRecordCount = "10";
    public const string InvalidByteCount = "11";
    public const string AccessMethodFailure = "12";
    public const string DuplicateFile = "13";
    public const string FileDirectionRefused = "14";
    public const string CipherSuiteNotSupported = "15";
    public const string EncryptedFileNotAllowed = "16";
    public const string UnencryptedFileNotAllowed = "17";
    public const string CompressionNotAllowed = "18";
    public const string SignedFileNotAllowed = "19";
    public const string UnsignedFileNotAllowed = "20";
    public const string InvalidFileSignature = "21";
    public const string FileDecryptionFailure = "22";
    public const string FileDecompressionFailure = "23";
    public const string UnspecifiedReason = "99";
}

/// <summary>Virtual file format (SFIDFMT).</summary>
public static class FileFormats
{
    public const string Fixed = "F";
    public const string Variable = "V";
    public const string Unstructured = "U";
    public const string Text = "T";

    /// <summary>
    /// Fixed and variable files are transferred as a sequence of records, unstructured and text files as one
    /// record (RFC 5024, sections 5.3.3 and 7.2).
    /// </summary>
    public static bool IsRecordStructured(string format) => format is Fixed or Variable;

    public static bool IsSupported(string format) => format is Fixed or Variable or Unstructured or Text;
}

/// <summary>File security level (SFIDSEC).</summary>
public static class SecurityLevels
{
    public const string None = "00";
    public const string Encrypted = "01";
    public const string Signed = "02";
    public const string EncryptedAndSigned = "03";
}

/// <summary>Cipher suite (SFIDCIPH).</summary>
public static class CipherSuites
{
    public const string None = "00";
    public const string TripleDesSha1 = "01";
    public const string Aes256Sha1 = "02";
    public const string TripleDesSha256 = "03";
    public const string Aes256Sha256 = "04";
    public const string TripleDesSha512 = "05";
    public const string Aes256Sha512 = "06";
    public const string Aes256Sha3512 = "07";
}

/// <summary>File compression algorithm (SFIDCOMP).</summary>
public static class FileCompressionAlgorithms
{
    public const string None = "0";
    public const string Zlib = "1";
}

/// <summary>File enveloping format (SFIDENV).</summary>
public static class FileEnvelopingFormats
{
    public const string None = "0";
    public const string Cms = "1";
}

/// <summary>Send / receive capabilities (SSIDSR).</summary>
public static class SendReceiveCapabilities
{
    public const string SendOnly = "S";
    public const string ReceiveOnly = "R";
    public const string Both = "B";
}
