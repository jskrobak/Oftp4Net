# Oftp4Net Server

[![CI](https://github.com/jskrobak/Oftp4Net/actions/workflows/ci.yml/badge.svg)](https://github.com/jskrobak/Oftp4Net/actions/workflows/ci.yml)

OFTP2 (ODETTE File Transfer Protocol 2.0, [RFC 5024](https://www.rfc-editor.org/rfc/rfc5024)) server and client
with a Blazor administration UI. Runs on .NET 10 with PostgreSQL.

## Features

- Sending files from a send queue to partners (initiator role), with retries and exponential back-off
- Receiving files on configurable TCP / TLS listeners (responder role)
- Both directions within one session (speaker / listener with change direction)
- End to End Responses: EERP is sent for received files and processed for sent files (status `DELIVERED`), NERP is handled
- TLS with the system trust store, a custom CA or a pinned partner certificate; optional client certificates
- Web UI: identities, partners, certificates, listeners, send queue, received files, settings and a live log

Not supported yet: secure authentication (AUCH/AURP), restart of interrupted transfers, buffer compression when sending,
file level security (CMS encryption, signing, compression) and signed EERP. Such files are refused with a proper SFNA
reason code.

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

## Setting up a partner

1. *Identities*: create your own identity (SSID code, SFID code, password you send to partners).
2. *Certificates*: import the TLS server certificate with its private key (PFX) and, if needed, the partner's certificate or CA.
3. *Listeners*: create a listener (port 6619 for TLS), assign the identity and the server certificate.
4. *Partners*: add the partner with its SSID/SFID codes, the password it sends to you, host and port.
5. *Send queue*: add a file (path on the server) addressed to the partner.

## Tests

```bash
dotnet test
```

## License

[MIT](LICENSE)
