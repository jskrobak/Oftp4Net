using Oftp4Net.Core.Protocol;

namespace Oftp4Net.Services.Security;

/// <summary>
/// File level security failed. <see cref="ReasonCode"/> is the OFTP answer reason code reported to the partner
/// when the file was refused because of it.
/// </summary>
public sealed class FileSecurityException : Exception
{
    public FileSecurityException(string message, Exception? innerException = null)
        : this(AnswerReasonCodes.UnspecifiedReason, message, innerException)
    {
    }

    public FileSecurityException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
