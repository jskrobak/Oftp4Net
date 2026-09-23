# Oftp4Net Server

[![CI](https://github.com/jskrobak/Oftp4Net/actions/workflows/ci.yml/badge.svg)](https://github.com/jskrobak/Oftp4Net/actions/workflows/ci.yml)

OFTP2 (ODETTE File Transfer Protocol 2.0, [RFC 5024](https://www.rfc-editor.org/rfc/rfc5024)) server and client
with a Blazor administration UI. Runs on .NET 10 with PostgreSQL.

## Features

- Sending files from a send queue to partners (initiator role), with retries and exponential back-off; sessions with
  several partners run in parallel
- Receiving files on configurable TCP / TLS listeners (responder role), sessions in parallel with an optional
  limit per listener
- Both directions within one session (speaker / listener with change direction)
- End to End Responses: EERP for delivered files, NERP for those that did not reach their destination or were not
  accepted (e.g. an unusable OFTP2 Communication Setup), both directions
- TLS with the system trust store, the Odette trust list (TSL), a custom CA or a pinned partner certificate; optional
  client certificates, with the validity and the revocation list of every certificate checked
- Received files stored where their virtual file name says: rules route them into their own directories, what
  matches none stays in the receive directory
- Character set conversion per partner: files are sent in ANSI or EBCDIC and received EBCDIC content is converted to ANSI
- File level security per partner: CMS signing, zlib compression, encryption and signed End to End Responses,
  cipher suites 01 - 10
- Certificates assigned per station and purpose where one certificate does not serve everything, with roll-over
- Revocation lists of the certification authorities downloaded and kept, with the periods between updates and the
  maximum age of a list as settings
- Secure authentication (SSIDAUTH with SECD/AUCH/AURP): both sides prove they hold the private key of their certificate
- Restart of interrupted transfers and ODETTE-FTP buffer compression, both negotiated per partner
- Partners on the older ODETTE-FTP 1.2 - 1.4 (RFC 2204) with their own command layout
- REST API with bearer tokens and webhooks, scripts run on transfer events, persistent transfer log
- Partner Details Exchange (PDX, Odette OFTP2 Communication Setup): partners set up and updated from their datasheets,
  received by e-mail or over OFTP, our own datasheet exported and sent
- Automatic exchange of certificates over OFTP (ODETTE_CERTIFICATE_DELIVER, _REQUEST and _REPLACE): renewals,
  roll-overs and replacements taken over without the administrator
- Certificate signing requests (CSR) with the profile of the OFTP2 Certificate Policy, for the Odette CA and others
- A script that creates the certificates of the Odette interoperability tests, including the ones that must be refused
- Import of partners from an existing OS4X installation
- Web UI: identities, partners, certificates, listeners, send queue, received files, settings and a live log

## OFTP2 support

What of RFC 5024 the implementation covers:

| Area | State |
|---|---|
| Session: SSRM, SSID, ESID, CD, credit (CDT) | Protocol levels 1 - 5 negotiated down to the lower one, buffer size and credit negotiated |
| Files: SFID, SFPA, SFNA, DATA, EFID, EFPA, EFNA | Both directions in one session, several files per session |
| File formats (SFIDFMT) | `U`, `T`, `F` and `V`, with record boundaries and record counts |
| End to end responses: EERP, NERP, RTR | Both are sent for received files and processed for sent ones |
| Buffer compression (SSIDCMPR) | Sent compressed when agreed, always accepted from a partner |
| Restart (SSIDREST, SFIDREST) | Interrupted transfers continue at the last complete 1K block |
| Secure authentication (SSIDAUTH, SECD, AUCH, AURP) | Both directions, challenge in a CMS envelope |
| File level security (SFIDSEC, SFIDCIPH, SFIDCOMP, SFIDENV) | Signing, zlib compression and encryption, cipher suites 01 – 10 (08 – 10 with RSA-PSS and RSA-OAEP) |
| Certificate exchange (ODETTE_CERTIFICATE_DELIVER, _REQUEST, _REPLACE) | Sent and received, assigned per station and purpose, answered with an EERP or a NERP |
| Certificate validation | Chain against the Odette trust list or the system, revocation lists read and kept here, and the logical identification data of the partner |
| Signed end responses (SFIDSIGN, EERPSIG, NERPSIG) | Requested, produced and verified, with the hash of the content |
| Transport | TCP/IP, with TLS 1.2 / 1.3 and optional client certificates |

Not implemented, because the deployments this server is built for do not use it:

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
self signed certificate created at the first start can be used for both TLS and file security. A station that uses
a different certificate for signing, encryption, end responses or authentication assigns them per purpose, see
*Certificates per station and purpose* below.

Each partner has (on the *Partners* page):

| Setting | Meaning |
|---|---|
| *Enable file compression* | the content is compressed with zlib (CMS CompressedData, SFIDCOMP=1) |
| *Enable file signing* | the content is signed with our file security certificate (SFIDSEC) |
| *Enable file encryption* | the content is encrypted for the partner's certificate (SFIDSEC) |
| *Enable secure authentication* | both sides authenticate each other after the SSID exchange (SSIDAUTH) |
| *Ask partner for a signed EERP or NERP* | the partner is asked to sign the end response of our files (SFIDSIGN) |
| *Cipher suite* | algorithms used for signatures, encryption and hashes (SFIDCIPH) |

The cipher suites are those of RFC 5024 and the extensions of the Odette OFTP2 Experts Group:

| Suite | Content encryption | Signature and key transport | Hash |
|---|---|---|---|
| `01`, `02` | 3DES-EDE-CBC, AES-256-CBC | RSA PKCS#1 v1.5 | SHA-1 |
| `03`, `04` | the same | the same | SHA-256 |
| `05`, `06` | the same | the same | SHA-512 |
| `07` | AES-256-CBC | the same | SHA3-512 |
| `08`, `09`, `10` | AES-256-CBC | RSA-PSS and RSA-OAEP | SHA-256, SHA-512, SHA3-512 |

`01` and `02` are supported by every OFTP2 node. The suites with SHA3 (`07` and `10`) need a platform that
provides it (Linux with OpenSSL 1.1.1+, recent Windows; not macOS, where they are hidden from the list).

The CMS classes of .NET know no signature algorithm for RSA-PSS with a SHA3 digest and no key transport with
OAEP and SHA3, although RSA itself does both, so the two packages of suite `10` are written and read by
`Sha3Cms` directly as RFC 5652 and RFC 4055 describe them. The datasheet of a partner (PDX) can
only announce the suites its schema knows, up to `07`; `08` to `10` are used with partners that agreed on them in
another way.

A partner can require files to be signed, encrypted or compressed (*Require … files from the partner*); a file
without it is refused before it is transferred. Sub-stations of a partner (other SFIDs reached through its connection,
see below) may override the settings of the partner.

A signed end response carries the hash of the transferred content (EERPHSH) and a CMS signature (EERPSIG). A response
we asked to be signed is only accepted when the signature is valid, made by the partner's certificate and covers the
hash of the content we sent; otherwise the file stays in the state *SENT* with the problem in the transfer log.

Files that cannot be unpacked are refused with the reason code that says what is wrong (cipher suite not supported,
decryption failure, invalid file signature, …). Signing, compression and encryption are done in memory, so files
larger than *Maximum size of a secured file (MB)* (setting, default 100) are not transferred to partners with file
security and the error is written to the transfer log.

## File formats

Every virtual file has a format (SFIDFMT), chosen on the send queue item or with `format` in the REST API:

| Format | Transfer | Stored here as |
|---|---|---|
| `U` unstructured | one record, the End of Record flag marks the end of the file | the file as it is |
| `T` text | the same; line separators are part of the data | the file as it is |
| `F` fixed records | each record ends with the End of Record flag, EFID reports their count | records one after another, the file size is a multiple of the record length |
| `V` variable records | the same, records may differ in length | each record with its length as two octets in network byte order in front of it |

`F` and `V` need a record length (SFIDLRECL): the length of every record, or of the longest one. The representation
of `V` is the one RFC 5024 prescribes in section 6.5 for variable files that are signed, compressed or encrypted,
so the same file works with and without file level security.

A signed, compressed or encrypted file has no discernable record boundaries, so it is transferred as unstructured
whatever SFIDFMT says (RFC 5024, section 5.3.3); the record count of the original file is still reported in EFID.
For the same reason a record structured file is never restarted in the middle — the restart position of such a
file is a record number, which is not supported.

Records of a `V` file cannot be converted to EBCDIC, because the lengths stored in the file are binary; the
transfer of such a file to a partner with the conversion switched on fails with a clear error.

## Certificate revocation

Certificates accepted through a chain — the Odette trust list or the operating system — are checked against the
revocation lists (CRL) of their issuers, in TLS connections and when a partner's certificate is validated; a
certificate on such a list is refused.

*Check certificate revocation (CRL)* switches the check on (default) and *Refuse a certificate whose revocation
state is unknown* decides what happens when the list cannot be read: by default an unreachable list is tolerated
so that transfers do not stop, with the stricter setting the certificate is refused as well.

A certificate pinned for a partner is trusted by itself and not through a chain, so no list is read for it.

The same applies to file level security: before a file is signed or encrypted and before a signature of a partner
is verified, the certificate is checked for its validity period and against the revocation list of its issuer. A
file secured with a certificate that must not be used any more is refused with the reason *file decryption failure*
or *invalid file signature*, and a queued file is not sent at all.

The lists of the certification authorities are downloaded from the addresses in the certificates and kept
(Odette OP08 2.6). *Read a revocation list again after (hours)* is the standard period between updates (default 24)
and *Maximum age of a revocation list (days)* the longest a list may go without being refreshed (default 15, as
Odette recommends); a certificate whose list is older is not used until a current one has been read. Both can be
put down for the interoperability tests. A list is only accepted when the authority that issued the certificate
signed it. TLS connections use the revocation check of the operating system, which has its own cache.

## Where received files are stored

A received file lands in the receive directory, in a sub-directory named after the code of its partner, and its
name is the virtual file name with the date and time of the virtual file behind it, so that nothing is ever
overwritten.

*Settings* → *Where received files are stored* routes files elsewhere by their virtual file name: the first rule
whose pattern matches decides, `*` stands for any number of characters and `?` for one, and upper and lower case
do not matter. A file that matches a rule is written into that directory, with the same name it would have in the
receive directory; one that matches no rule stays there.

| Pattern | Directory |
|---|---|
| `XXX*` | `/data/a` |
| `YYYY*` | `/data/b` |

The file is written to its place while it is transferred (next to it, with the extension `.part`, until the
transfer is complete), not copied there afterwards, and the path is the one the *Received files* page and
`GET /api/v1/inbox/{id}/content` serve. Should the name be taken in a routed directory, because another partner
sent a file of the same name at the same moment, a number is added. A directory that cannot be created is
reported in the log and the file stays in the receive directory instead of the transfer failing.

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

## Partner Details Exchange (PDX)

The Odette OFTP2 Communication Setup (Odette OP08 part 3, schema version 1.2) is a datasheet with everything needed
to set up a connection to a station: address, TLS, codes, password, security settings, sub-stations, contacts and
certificates.

**Importing a partner's datasheet.** *Partners* → *Import PDX* shows what would change before anything is written: a
partner with the same SSID is updated, otherwise a new one is created. The security settings of the datasheet are
compared with our *station profile* (*Settings*): *forbidden* turns a feature off, *required* turns it on (a
conflict when the other side forbids it), *optional* on both sides stays off, everything else is on. A datasheet
valid from a later time can be scheduled instead of being applied now.

**Datasheets received over OFTP** (virtual file `OFTP_COMMUNICATION_SETUP`) are checked before the end response: a
datasheet that cannot be used is answered with a NERP that says why; an accepted one is applied after its EERP has
been sent (the EERP still goes with the old settings), or at its *valid from* time. *Settings* → *Apply datasheets
received over OFTP* decides which ones are applied without the administrator: never, only signed with the partner's
certificate (default), or always. The others wait on the *Partner setups* page, where they are approved (EERP) or
rejected (NERP with a text). The page also keeps the history of all datasheets with the changes they made. When a
partner's certificate is replaced, the previous one is still accepted for signatures of files that were on their way.

**Our own datasheet.** *Identities* → *Export PDX* downloads it (e.g. for e-mail), *Partners* → *Send our datasheet*
sends it over OFTP: unencrypted, signed with our file security certificate when there is one, without a signed EERP.
It is built from the identity (other identities with the same SSID and their own SFID become sub-stations), the
station profile (company, contacts, the listener partners call and its public host) and our certificates. The REST
API returns it at `GET /api/v1/pdx/{identity}`.

Datasheets are written in version 1.2 of the schema, or in 1.1 for software that does not know 1.2 yet (*Partners* →
*Version of our datasheet*; taken over from a partner's own datasheet). OS4X (2025), for example, reads only 1.1 and
accepts a datasheet over OFTP only when it is signed with the certificate it has for the partner. Dates are written in
UTC as `+00:00`, the form every implementation tested reads correctly.

## Automatic exchange of certificates

Certificates are exchanged over OFTP as the virtual files `ODETTE_CERTIFICATE_DELIVER`, `ODETTE_CERTIFICATE_REQUEST`
and `ODETTE_CERTIFICATE_REPLACE` (Odette OP08 2.5). They carry one certificate in DER and are always transferred
unsecured (SFIDFMT `U`, SFIDSEC `00`, SFIDSIGN `N`), because the partner may not have our certificate yet.

The context menu of a partner (*Send our certificate*) puts one of our certificates into the send queue. A delivery
announces a new certificate while the old one stays valid, a replacement takes the old one out of use at once, and a
request asks the partner for its own certificate in return. *Replaces* writes the identification data (CLID) of the
certificate the partner has so far into SFIDDESC, so that the partner can assign the new one even when its subject
or issuer changed.

A certificate that arrives is checked (validity period, chain of trust, revocation) and assigned to the partner it
belongs to: to the certificate named in SFIDDESC, or to the one with the same subject, issuer and key usages. A
delivered certificate takes the place of the one it replaces and the old one stays valid for the roll-over period,
a replacement ends its use at once, and a request is answered with our certificate — in the same session when we
still get the turn. The partner gets an EERP for a certificate that was taken over; one that cannot be assigned is
answered with a NERP carrying the reason, as the specification requires, and has to be sorted out by hand.

*Settings* → *Take over certificates received over OFTP* decides how much is done without the administrator: only
certificates that replace one the partner already has here (default), those plus a first certificate whose chain
ends with a trusted certification authority, or nothing at all. A self signed certificate is only accepted as the
renewal of one that is already configured, as OP08 requires.

## Certificates per station and purpose

A station that uses one certificate for everything needs nothing beyond the *Partner certificate* of a partner and
the file security certificate in the settings. Where that is not enough, certificates are assigned per purpose —
file signatures, file encryption, end response signatures and secure authentication — and, for a partner, per
sub-station (Odette OP08 2.5). The partner dialog holds the certificates of the partner, the identity dialog ours;
what is not assigned falls back to the single certificate.

The assignment decides which certificate signs and encrypts a file, verifies the signature of a received file,
signs and verifies End to End Responses and answers an authentication challenge. A roll-over keeps the replaced
certificate of the assignment valid until it expires.

A datasheet that names a different certificate per feature is imported the same way, and the automatic exchange
moves a received certificate on exactly where the one it replaces was used: an ODETTE_CERTIFICATE_DELIVER for the
signing certificate of a sub-station leaves the encryption certificate and the certificates of other stations
alone. A certificate request is answered with every certificate of the station addressed, each in its own file as
the specification requires.

## Odette trust list (TSL)

Certificates issued by the certification authorities of the Odette Trust Service Status List are trusted for TLS and
in partner datasheets without being added one by one. *Settings* → *Odette trust list* sets the address (production
`TSL_OFTP2.XML` or the test list for the interoperability tests) and how often it is downloaded. The list is signed:
its signature is verified and the certificate it is signed with is pinned at the first download, a list signed by
another certificate is refused until the pinned signer is cleared. A local copy is used when the download fails. On a
server without access to the internet, switch the trust list off.

Every entry of the list carries the OFTP2 certification authority and its root, which is there to verify that
authority only. A certificate issued directly by such a root is refused although its chain is sound, because only
the listed authority may issue the certificate of a partner (Odette OP08 2.7).

## Certificates for the interoperability tests

[`samples/odette-test-pki.sh`](samples/odette-test-pki.sh) creates the certificates of the Odette interoperability
tests (OP09 chapter 4.6) with OpenSSL: a root and an OFTP2 authority for company A and for company B, the
certificates CA01 - CA14 and CB01, CB04 - CB06 with the subjects and key usages the test cases prescribe, and a
revocation list per authority.

```bash
samples/odette-test-pki.sh --out ./pki --crl-url https://crl.example.com --host-a oftp.example.com --id-a O0013000000MYCOMPANY
```

Publish the `crl` directory under that address, send the four authority certificates to Odette for the test list
and import the PKCS#12 bundles of your own stations. `--revoke CA12` revokes one certificate and writes the list
again, which is what test case 7.1 needs. The certificate CB05 is issued by the root directly and CB06 by an
authority outside the trust list; both are meant to be refused (test cases 7.2 and 7.3).

## Requesting a certificate

*Certificates* → *Request certificate* creates a key pair on the server and a certificate signing request (CSR) for a
certification authority of the TSL, e.g. the [Odette CA](https://www.odette.org/services/odette-ca). The request has
the profile of the OFTP2 Certificate Policy: RSA (2048, 3072 or 4096 bit) with SHA-256, the host name partners call as
common name and subject alternative name, the Odette ID (SSID) as serial number, key usage digital signature and key
encipherment and the extended key usages TLS server and client authentication. The form is filled in from the station
profile (*Settings*).

Download the CSR and submit it to the CA. When the signed certificate arrives (PEM, DER or PKCS#7, with or without its
chain), *Import certificate* at the request stores it together with the private key; the key never leaves the server
and is removed from the request. The certificate then serves as TLS server certificate of a listener, TLS client
certificate and file security certificate at the same time.

Public TLS certificate authorities no longer issue certificates for TLS client authentication (from 2026) and shorten
their validity to months, so a certificate of a CA specialised in OFTP2 (Odette CA, mendelson CA and others of the TSL)
is the better choice.

## Setting up a partner

1. *Identities*: create your own identity (SSID code, SFID code, password you send to partners).
2. *Certificates*: request a certificate from a CA (*Request certificate*) or import one with its private key (PFX), and,
   if needed, the partner's certificate or CA.
3. *Listeners*: create a listener (port 6619 for TLS), assign the identity and the server certificate.
4. *Partners*: add the partner with its SSID/SFID codes, the password it sends to you, host and port, and, for a
   mainframe partner, the character set conversion — or import its datasheet (*Import PDX*) and send it ours.
5. *Partners*: if the partner requires file level security, assign its certificate and switch on signing,
   compression or encryption; *Settings* holds our own certificate used for it.
6. *Send queue*: add a file (path on the server) addressed to the partner.

## Tests

```bash
dotnet test
```

## Support

Commercial support and hosting are available at [oftp4net.com](https://oftp4net.com).

## License

[MIT](LICENSE)
