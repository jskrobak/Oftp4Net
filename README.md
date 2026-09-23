# Oftp4Net Server

[![CI](https://github.com/jskrobak/Oftp4Net/actions/workflows/ci.yml/badge.svg)](https://github.com/jskrobak/Oftp4Net/actions/workflows/ci.yml)

OFTP2 (ODETTE File Transfer Protocol 2.0, [RFC 5024](https://www.rfc-editor.org/rfc/rfc5024)) server and client
with a Blazor administration UI. Runs on .NET 10 with PostgreSQL.

## Features

- Sending files from a send queue to partners (initiator role), with retries and exponential back-off
- Receiving files on configurable TCP / TLS listeners (responder role)
- Both directions within one session (speaker / listener with change direction)
- End to End Responses: EERP for delivered files, NERP for those that did not reach their destination, both directions
- TLS with the system trust store, a custom CA or a pinned partner certificate; optional client certificates
- Character set conversion per partner: files are sent in ANSI or EBCDIC and received EBCDIC content is converted to ANSI
- File level security per partner: CMS signing, zlib compression, encryption and signed End to End Responses
- Secure authentication (SSIDAUTH with SECD/AUCH/AURP): both sides prove they hold the private key of their certificate
- Restart of interrupted transfers and ODETTE-FTP buffer compression, both negotiated per partner
- Partners on the older ODETTE-FTP 1.2 - 1.4 (RFC 2204) with their own command layout
- REST API with bearer tokens and webhooks, scripts run on transfer events, persistent transfer log
- Import of partners from an existing OS4X installation
- Web UI: identities, partners, certificates, listeners, send queue, received files, settings and a live log

## OFTP2 support

What of RFC 5024 the implementation covers:

| Area | State |
|---|---|
| Session: SSRM, SSID, ESID, CD, credit (CDT) | Protocol levels 1 - 5 negotiated down to the lower one, buffer size and credit negotiated |
| Files: SFID, SFPA, SFNA, DATA, EFID, EFPA, EFNA | Both directions in one session, several files per session |
| End to end responses: EERP, NERP, RTR | Both are sent for received files and processed for sent ones |
| Buffer compression (SSIDCMPR) | Sent compressed when agreed, always accepted from a partner |
| Restart (SSIDREST, SFIDREST) | Interrupted transfers continue at the last complete 1K block |
| Secure authentication (SSIDAUTH, SECD, AUCH, AURP) | Both directions, challenge in a CMS envelope |
| File level security (SFIDSEC, SFIDCIPH, SFIDCOMP, SFIDENV) | Signing, zlib compression and encryption, cipher suites 01 – 06 |
| Signed end responses (SFIDSIGN, EERPSIG, NERPSIG) | Requested, produced and verified, with the hash of the content |
| Transport | TCP/IP, with TLS 1.2 / 1.3 and optional client certificates |

Not implemented, because the deployments this server is built for do not use it:

- Record structured virtual files: files are transferred as unstructured (SFIDFMT `U`), the record format of a
  partner is accepted but records are not interpreted and no record count is reported in EFID
- Broadcast and distribution to several destinations through an intermediate location
- Special logic (SSIDSPEC)
- Transports other than TCP/IP (X.25, ISDN) and the mailbox operation of older OFTP versions

## Solution structure

| Project | Content |
|---|---|
| `Oftp4Net.Core` | Protocol library without application dependencies: commands, framing, session state machine, TCP/TLS transport |
| `Oftp4Net.Core.Tests` | Encoding tests and end-to-end sessions over TCP and TLS |
| `Oftp4Net.Domain` | Entities |
| `Oftp4Net.Entity` | EF Core DbContext (PostgreSQL) and migrations |
| `Oftp4Net.DataLayer` | Repositories and filters (Havit.Data patterns) |
| `Oftp4Net.Services` | Send queue processing, listeners, session handler bridging the protocol and the database |
| `Oftp4Net.DependencyInjection` | Data layer registration |
| `Oftp4Net.Server` | Blazor Server UI and host |

## Configuration

| Key | Description |
|---|---|
| `ConnectionStrings:Oftp4Net` | PostgreSQL connection string (required) |
| `Database:MigrateOnStartup` | Create / update the database schema on startup (default `true`) |
| `DataProtection:KeysDirectory` | Keys encrypting the auth cookie and the passwords stored in the database. **Back them up together with the database**, without them stored passwords cannot be decrypted. |
| `DataDirectory` | Base directory for relative receive / outbox directories (default: current directory, `/data` in Docker) |
| `LogDirectory` | Directory of the rolling log file |
| `Upload:MaxOutboxFileSizeMB` | Maximum size of a file uploaded to the send queue (default 512) |
| `Tls:GenerateCertificate` | Create a self-signed TLS certificate on startup when there is none with a private key (default `true`) |
| `Tls:CertificateSubject` | Host name in the generated certificate (default: machine / container name) |
| `ReverseProxy:TrustAll` | Trust `X-Forwarded-*` headers from any proxy |

Runtime settings (receive directory, send interval, retry count, buffer size, credit, timeouts, TLS client certificate)
are edited on the *Settings → General* page and stored in the database.

## Users

Users of the administration UI are stored in the database (*Settings → Users*), passwords are hashed.
When the application starts with an empty database it creates the account **`admin` / `admin`**; the password has to be
changed after the first sign in.

## Running locally

```bash
dotnet run --project Oftp4Net.Server
```

The development configuration (`appsettings.Development.json`) uses PostgreSQL on `localhost:5432`
(database `oftp4net-dev`, user `root` / `root`). The database and its schema are created on startup.

In Development the self-signed certificate from [`certs/`](certs/README.md) (`CN=localhost`, development only)
is imported to the database on startup and can be used as the server certificate of a TLS listener.

### Sending a file to yourself

In Development the database is also seeded (`SeedLoopback` in `appsettings.Development.json`) with:

- identity **Loopback** (SSID/SFID `O0013000000LOOPBACK`, password `LOOPBACK`),
- listener **Loopback TLS** on `127.0.0.1:16619` with the development certificate,
- partner **Loopback** pointing to `127.0.0.1:16619` over TLS, trusting the development certificate.

To try a transfer open *Queues → Send queue*, click *New item*, upload a file (e.g. [`samples/hello.edi`](samples/hello.edi)),
select identity and partner *Loopback*, save and click *Send now*.
The file appears in *Queues → Received files* and the queue item turns `DELIVERED` once the EERP arrives.

New migrations are created with the EF tool pinned in `dotnet-tools.json`:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project Oftp4Net.Entity
```

## Docker

Images for `linux/amd64` and `linux/arm64` are published to the GitHub Container Registry:
`latest` from the `main` branch, `X.Y.Z` / `X.Y` for release tags `vX.Y.Z` and `sha-…` for every build.

```bash
docker run -p 8080:8080 -p 6619:6619 -v oftp4net-data:/data \
  -e ConnectionStrings__Oftp4Net="Host=db;Database=oftp4net;Username=oftp;Password=..." \
  ghcr.io/jskrobak/oftp4net:latest
```

On the first start the container creates its own self-signed TLS certificate (RSA 3072, 3 years) when the database
contains no certificate with a private key. Set the host name partners use with `Tls__CertificateSubject`
(default: the container host name). The certificate is used as the TLS client certificate and can be selected as the
server certificate of a listener; its public part is written to `/data/certs/server.crt` and can be downloaded on the
*Certificates* page to send it to partners.

All persistent data lives in the `/data` volume (`DataDirectory`): data protection keys, logs and the receive and
outbox directories (relative paths in the settings are resolved against it).

To build the image locally:

```bash
docker build -f Oftp4Net.Server/Dockerfile -t oftp4net-server .
```

## Transfer log

The *Logs* section shows the transfer history stored in the database, in four views with filters (partner, virtual
file name, severity, period) and a detail of every record:

| View | Records |
|---|---|
| Outgoing | connections we open (start, end, failures, rejected authentication) and sent files (sent, failed, retries) |
| Incoming | connections partners open, including failed TLS handshakes, and received or refused files |
| EERP / NERP | End to End Responses sent for received files and received for sent files |
| Hooks and webhooks | every hook run with exit code, duration and output, and every webhook call |

Records older than *Archive transfer log after (days)* (setting, default 90) are archived: they are kept, but shown only
when *Complete archive* is checked in the filter. *Log stream* remains the live technical log.

## Character set conversion

Mainframe partners exchange files in EBCDIC. Every partner therefore has (on the *Partners* page):

| Setting | Meaning |
|---|---|
| *Encoding of outgoing files* | `ANSI` sends the file as it is stored, `EBCDIC` converts its content while it is sent |
| *Convert incoming EBCDIC to ANSI* | converts the content of files received from the partner while they are stored |
| *ANSI code page* | code page files are stored in on this server (default Windows-1252) |
| *EBCDIC code page* | EBCDIC variant of the partner (default IBM500 International) |

Both sides are single byte code pages, so the conversion maps octet to octet and does not change the size of the
file. Characters the target code page does not contain are replaced by a question mark. The EBCDIC new line (0x15),
which has no counterpart in the ANSI code pages, becomes a line feed when a received file is converted.

The conversion is driven only by the setting: OFTP does not tell which character set the content of a virtual file
uses, so *Convert incoming EBCDIC to ANSI* is to be set for partners that send EBCDIC.

## File level security

The content of a virtual file can be signed, compressed and encrypted (RFC 5024, section 6). Each step wraps the
content in a CMS package and they are applied in this order; a received file is unpacked in the reverse order.

Two certificates are involved:

| Certificate | Where | Used for |
|---|---|---|
| ours, with private key | *Settings* → *File security certificate* | signing files and end responses, decrypting received files |
| the partner's, public part | *Partners* → *Partner certificate* | encrypting files for the partner, verifying its signatures |

Give the public part of your certificate to the partner and import theirs on the *Certificates* page. The
self signed certificate created at the first start can be used for both TLS and file security.

Each partner has (on the *Partners* page):

| Setting | Meaning |
|---|---|
| *Enable file compression* | the content is compressed with zlib (CMS CompressedData, SFIDCOMP=1) |
| *Enable file signing* | the content is signed with our file security certificate (SFIDSEC) |
| *Enable file encryption* | the content is encrypted for the partner's certificate (SFIDSEC) |
| *Enable secure authentication* | both sides authenticate each other after the SSID exchange (SSIDAUTH) |
| *Ask partner for a signed EERP or NERP* | the partner is asked to sign the end response of our files (SFIDSIGN) |
| *Cipher suite* | algorithms used for signatures, encryption and hashes (SFIDCIPH) |

The cipher suites are those of RFC 5024 and its common extensions; `01` (3DES, SHA-1) and `02` (AES-256, SHA-1) are
supported by every OFTP2 node, `03`–`06` use SHA-256 or SHA-512.

A signed end response carries the hash of the transferred content (EERPHSH) and a CMS signature (EERPSIG). A response
we asked to be signed is only accepted when the signature is valid, made by the partner's certificate and covers the
hash of the content we sent; otherwise the file stays in the state *SENT* with the problem in the transfer log.

Files that cannot be unpacked are refused with the reason code that says what is wrong (cipher suite not supported,
decryption failure, invalid file signature, …). Signing, compression and encryption are done in memory, so files
larger than *Maximum size of a secured file (MB)* (setting, default 100) are not transferred to partners with file
security and the error is written to the transfer log.

## Files that cannot be delivered

A file that arrives correctly but cannot be handed over to its final destination — the ERP system refuses it, the
addressee does not exist, the content cannot be processed — is reported to the partner with a Negative End Response
(NERP) instead of the positive one.

On the *Received files* page the warning icon of a received file opens a dialog with the answer reason code
(NERPREAS) and a description (NERPREAST); the REST API does the same with
`POST /api/v1/inbox/{id}/not-delivered` and the body `{ "reasonCode": "02", "reasonText": "..." }`, which is the
way an integration reports that it could not process the file.

The file changes to the state `NOT_DELIVERED` and the response is sent in the next session with the partner, signed
when the partner asked for a signed end response. Only a file whose end response has not been sent yet can be
reported this way. In the other direction, a NERP from a partner puts the queue item into `NOT_DELIVERED` and runs
the `OnNotDelivered` hook.

## Partners on ODETTE-FTP 1.x

The release each partner gets is set on the *Partners* page (*ODETTE-FTP release*), and the session runs at the
lower of the two levels announced in SSIDLEV:

| Level | Revision | What it means here |
|---|---|---|
| 5 | 2.0 | everything described in this file |
| 4 | 1.4 | no file level security, secure authentication, signed end responses, file description or reason texts |
| 2 | 1.3 | additionally short stamps (`YYMMDD` / `HHMMSS`) and no NERP |
| 1 | 1.2 | the level RFC 2204 announces, same layout as 1.3 here |

Below OFTP 2.0 the commands use the layout of RFC 2204: the reserved area before the stamps is longer, file size,
restart position and the counts of EFID are shorter, and SSID has no secure authentication field. Answer reason
codes the release does not know (the file security codes, and 14 before revision 1.4) are sent as `99`.

A file with signing, compression or encryption is not sent to such a partner at all — the transfer fails with a
clear error instead of quietly dropping the protection. A file that cannot be delivered is reported with a NERP
from revision 1.4 on; below it the transfer log says that the partner cannot be told.

Only ODETTE-FTP over TCP/IP is supported, the X.25 and ISDN parts of the old specification are not.

## Buffer compression and restart

Two session capabilities are offered per partner (on the *Partners* page) and used only when the partner offers
them as well; what the partner sends is always accepted.

| Setting | Meaning |
|---|---|
| *Enable buffer compression* | runs of equal octets are compressed in the data exchange buffers (SSIDCMPR) |
| *Enable restart of interrupted transfers* | an interrupted file continues where it stopped (SSIDREST) |

Buffer compression is the compression of ODETTE-FTP itself: a run of up to 63 equal octets is sent as a single
one. It helps with padded records and costs nothing on content that does not repeat. It is independent of the file
compression of file level security, which compresses the whole file with zlib.

A transfer that is interrupted (connection loss, shutdown) leaves the received part on disk, the file keeps the
state *INTERRUPTED* and the queue item remembers how far it got. The next attempt offers that position in SFID,
the receiver answers in SFPA with the position it really has (complete 1K blocks only) and the transfer continues
there, keeping the original virtual file date and time, which identify the file for the partner. Signed, compressed
or encrypted files are always sent from the beginning, because their content is built anew for every attempt.

## Secure authentication

With *Enable secure authentication* the session continues after the SSID exchange with the authentication phase of
RFC 5024, section 4.2.3: each side sends a challenge (a 20 byte random number in a CMS envelope for the certificate
of the other side, AUCH) and expects it back decrypted (AURP). The initiator is challenged first, then the roles are
swapped with a Security Change Direction (SECD). The same two certificates as for file level security are used.

Secure authentication is not negotiated: both sides have to require it, otherwise the session is ended with reason
code `12` (*secure authentication requirements incompatible*). A wrong answer to a challenge ends the session with
reason code `11`. Because it authenticates the identity behind the certificate, it is worth switching on in addition
to the TLS connection and the SSID password.

## REST API

Integrations can use a REST API authenticated with a bearer token: `Authorization: Bearer <token>`.
Tokens are created in *Settings → API tokens* and shown only once (only their hash is stored).

*Settings → REST API* shows the interactive documentation (Scalar) with request examples in several languages;
the OpenAPI description itself is at `/openapi/v1.json`. Both require a signed in administrator.

| Endpoint | Purpose |
|---|---|
| `POST /api/v1/outbox` | puts a file into the send queue (multipart: `file`, `partner`, `identity`, optional `virtualFileName`, `description`, `reference`, `webhookUrl`, `webhookSecret`) |
| `GET /api/v1/outbox` | lists queued files (`status`, `partner`, `reference`, `from`, `to`, `skip`, `take`) |
| `GET /api/v1/outbox/{id}` | detail of a queued file |
| `DELETE /api/v1/outbox/{id}` | removes a file that has not been transferred yet |
| `POST /api/v1/outbox/{id}/retry` | puts a failed or finished file back into the queue |
| `GET /api/v1/inbox` | lists received files; `onlyNew=true` returns files not fetched yet |
| `GET /api/v1/inbox/{id}` / `…/content` | detail / content of a received file |
| `POST /api/v1/inbox/{id}/fetched` | marks a received file as fetched |
| `GET /api/v1/partners`, `/identities` | codes usable when sending |
| `GET /api/v1/events` | reads the transfer log |
| `GET /api/v1/status` | state of the services, listeners and queues |

```bash
curl -H "Authorization: Bearer $TOKEN" -F file=@orders.edi -F partner=O0013000000PARTNER \
     -F identity=O0013000000ME -F reference=ORDER-4711 \
     -F webhookUrl=https://erp.example.com/oftp/callback -F webhookSecret=$SECRET \
     https://oftp.example.com/api/v1/outbox
```

### Webhooks

`webhookUrl` registered with a file is called on `file.sent`, `file.delivered` (EERP arrived), `file.not_delivered`
(NERP) and `file.send_failed`. A token can also carry an *inbox webhook URL*, called on `file.received`.

The request is a `POST` with a JSON body (`event`, `timestamp`, `queueItemId` or `receivedFileId`, `reference`,
`virtualFileName`, `fileDate`, `fileTime`, `fileSize`, `partnerName`, `partnerSsid`, `originator`, `destination`,
`status`, `error`, `sentDate`, `deliveredDate`) and the header `X-Oftp4Net-Event`. When a secret is set, the header
`X-Oftp4Net-Signature` contains `sha256=<hex>`, the HMAC-SHA256 of the body; verify it before trusting the call.
A call that fails is retried (`Webhooks:RetryDelaysSeconds`, default after 5 s, 30 s and 2 min) and the result is in
*Logs → Hooks and webhooks*. URLs in private or loopback networks are refused unless
`Webhooks:AllowPrivateNetworks` is enabled.

## Hooks

A script or executable can be run on protocol events. Hooks are configured in the application configuration
(not in the web UI), e.g. with environment variables:

| Setting | Runs when |
|---|---|
| `Hooks:OnReceived` | a file was received and stored |
| `Hooks:OnReceiveFailed` | receiving a file failed |
| `Hooks:OnSent` | a file was transferred and the partner confirmed receipt (EFPA) |
| `Hooks:OnSendFailed` | sending a file failed (`OFTP_WILL_RETRY` tells whether it will be retried) |
| `Hooks:OnDelivered` | the partner confirmed delivery to the final destination (EERP) |
| `Hooks:OnNotDelivered` | the partner reported that the file could not be delivered (NERP) |
| `Hooks:TimeoutSeconds` | a script running longer is killed (default 60) |

Parameters are passed as environment variables and, with the same names in camel case, as a JSON object on
standard input:

| Variable | Events | Content |
|---|---|---|
| `OFTP_EVENT`, `OFTP_TIMESTAMP` | all | event name, time (ISO 8601) |
| `OFTP_PARTNER_NAME`, `OFTP_PARTNER_SSID`, `OFTP_PARTNER_SFID` | all | partner |
| `OFTP_VIRTUAL_FILE_NAME`, `OFTP_FILE_DATE`, `OFTP_FILE_TIME` | all | virtual file identification |
| `OFTP_ORIGINATOR`, `OFTP_DESTINATION` | all | SFID codes of the file's originator and destination |
| `OFTP_FILE_PATH`, `OFTP_FILE_SIZE`, `OFTP_DESCRIPTION`, `OFTP_STATUS` | all | local file, size in bytes, description, new status |
| `OFTP_RECEIVED_FILE_ID`, `OFTP_USER_DATA` | received | record id, user data field |
| `OFTP_QUEUE_ITEM_ID`, `OFTP_IDENTITY_NAME` | sent, send failed, (not) delivered | send queue item, our identity |
| `OFTP_ERROR` | failures | error message |
| `OFTP_REASON_CODE`, `OFTP_REASON_TEXT` | send failed, not delivered | answer reason from SFNA / EFNA / NERP |
| `OFTP_WILL_RETRY`, `OFTP_RETRY_COUNT`, `OFTP_NEXT_RETRY` | send failed | retry state |
| `OFTP_CREATOR` | not delivered | node that created the NERP |

Hooks run in the background one after another; a slow or failing script never affects the transfer. Their output and
exit code are logged. See [`samples/hooks/on_received.sh`](samples/hooks/on_received.sh). In Docker, mount the
scripts and point the configuration to them:

```bash
docker run ... -v ./hooks:/scripts:ro -e Hooks__OnReceived=/scripts/on_received.sh ghcr.io/jskrobak/oftp4net:latest
```

## Importing partners from OS4X

*Partners* → *Import from OS4X* reads the partner table of an OS4X installation (MariaDB / MySQL) and creates the
partners that are not here yet. The dialog asks for the connection (host, port, database, user, password and the
table prefix from `os4x.conf`, by default `os4x_`); the password is used for that one import and is not stored.

The list that appears is a preview: nothing is written until *Import selected* is pressed. Each row says what will
happen — *new*, *already exists* (an existing partner is never changed) or *cannot be imported* with the reason,
for example an OFTP 1.x partner.

OS4X keeps both sides of a relation in one row, so the identity of a partner comes from the same record: its
`my_ssid` / `my_sfid` / `my_password` become an identity here, reused when one with that code already exists.

| OS4X | Oftp4Net |
|---|---|
| `shortname`, `longname` | name and description |
| `his_ssid`, `his_sfid`, `his_password` | partner codes and password |
| `my_ssid`, `my_sfid`, `my_password` | identity |
| `address`, `port` / `port_tls`, `use_tls` | host, port and TLS |
| `oftp2_cipher_suite` | cipher suite (`01`–`06`) |
| `oftpv2_sign`, `oftpv2_encrypt`, `oftp2_compression_level` | file signing, encryption and compression |
| `oftpv2_sec_auth_req`, `oftpv2_req_sig_eerp` | secure authentication, signed end responses |

Certificates are not part of the OS4X partner table, so the trusted certificate of a TLS connection and the
partner's certificate for file security are assigned after the import. Buffer size and credit are per partner in
OS4X but global here, so they are not taken over.

## Setting up a partner

1. *Identities*: create your own identity (SSID code, SFID code, password you send to partners).
2. *Certificates*: import the TLS server certificate with its private key (PFX) and, if needed, the partner's certificate or CA.
3. *Listeners*: create a listener (port 6619 for TLS), assign the identity and the server certificate.
4. *Partners*: add the partner with its SSID/SFID codes, the password it sends to you, host and port, and, for a
   mainframe partner, the character set conversion.
5. *Partners*: if the partner requires file level security, assign its certificate and switch on signing,
   compression or encryption; *Settings* holds our own certificate used for it.
6. *Send queue*: add a file (path on the server) addressed to the partner.

## Tests

```bash
dotnet test
```

## License

[MIT](LICENSE)
