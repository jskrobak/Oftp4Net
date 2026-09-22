# Development certificate

**For development and testing only. The private key is public (it is in this repository) – never use it in production.**

| File | Content |
|---|---|
| `oftp4net-dev.pfx` | Certificate with private key (PKCS#12), password `oftp4net-dev` |
| `oftp4net-dev.crt` | Public certificate (PEM), give it to partners that want to trust / pin it |

Subject `CN=localhost, O=Oftp4Net Development`, SAN `localhost`, `127.0.0.1`, `::1`, valid 10 years,
usable as TLS server and client certificate.

In the Development environment the certificate is imported to the database on startup (see `SeedCertificates`
in `Oftp4Net.Server/appsettings.Development.json`), so it can be selected as the server certificate of a listener.

Regenerate:

```bash
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes -keyout oftp4net-dev.key -out oftp4net-dev.crt \
  -subj "/CN=localhost/O=Oftp4Net Development" \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1,IP:::1" \
  -addext "extendedKeyUsage=serverAuth,clientAuth" -addext "keyUsage=digitalSignature,keyEncipherment"
openssl pkcs12 -export -inkey oftp4net-dev.key -in oftp4net-dev.crt -out oftp4net-dev.pfx \
  -passout pass:oftp4net-dev -name "Oftp4Net Development"
rm oftp4net-dev.key
```
