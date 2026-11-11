namespace Oftp4Net.Services.Mail;

/// <summary>How the application sends e-mail.</summary>
public enum MailProvider
{
    /// <summary>It does not send e-mail; texts can still be previewed.</summary>
    None,

    /// <summary>An SMTP server: any mail server, relay or mail service.</summary>
    Smtp,

    /// <summary>Microsoft Graph (Microsoft 365), for tenants where SMTP AUTH is switched off.</summary>
    Graph,
}

/// <summary>
/// E-mail of the application, configuration section <c>Mail</c> (e.g. <c>Mail__Provider=Smtp</c> in the environment).
/// Passwords and secrets belong to the environment or a file outside the image, as the other secrets.
/// </summary>
public sealed class MailOptions
{
    public const string SectionName = "Mail";

    public MailProvider Provider { get; set; } = MailProvider.None;

    /// <summary>
    /// Sender the recipients see (From). With Graph it may differ from <see cref="GraphMailOptions.Mailbox"/> when that
    /// mailbox may send as it (Send As); empty for the sending mailbox itself.
    /// </summary>
    public string? From { get; set; }

    /// <summary>Display name of the sender.</summary>
    public string? FromName { get; set; }

    /// <summary>Address the answers go to (Reply-To); empty for the sender.</summary>
    public string? ReplyTo { get; set; }

    /// <summary>A blind copy of every message, e.g. to keep them in a shared mailbox.</summary>
    public string? Bcc { get; set; }

    public SmtpMailOptions Smtp { get; set; } = new();

    public GraphMailOptions Graph { get; set; } = new();
}

public enum SmtpSecurity
{
    /// <summary>TLS on connect for port 465, STARTTLS when the server offers it otherwise.</summary>
    Auto,

    /// <summary>No encryption (only a relay in the own network).</summary>
    None,

    /// <summary>STARTTLS, required.</summary>
    StartTls,

    /// <summary>TLS from the start (port 465).</summary>
    SslOnConnect,
}

public sealed class SmtpMailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

    /// <summary>Empty for a relay that does not ask for a sign in.</summary>
    public string? UserName { get; set; }

    public string? Password { get; set; }
}

/// <summary>
/// Microsoft Graph with an app registration of its own (application permission Mail.Send, client credentials).
/// Restrict it to the sending mailbox in Exchange Online (RBAC for Applications), otherwise it may send as anybody.
/// </summary>
public sealed class GraphMailOptions
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>Mailbox the message is sent from (e.g. mailer@example.com); the sent copy stays in it.</summary>
    public string? Mailbox { get; set; }

    /// <summary>Sign in authority, for a cloud other than the public one.</summary>
    public string Authority { get; set; } = "https://login.microsoftonline.com";

    /// <summary>Graph endpoint, for a cloud other than the public one.</summary>
    public string Endpoint { get; set; } = "https://graph.microsoft.com";
}
