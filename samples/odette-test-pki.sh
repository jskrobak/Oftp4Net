#!/usr/bin/env bash
#
# Creates the certificates of the Odette OFTP2 interoperability tests (OP09 chapter 4.6) with OpenSSL:
# two hierarchies (company A and company B), the certificates CA01 - CA14 and CB01, CB04 - CB06, and a
# revocation list for every authority. The profile follows the OFTP2 Certificate Policy: RSA with SHA-256, the
# host name as common name and subject alternative name, the Odette ID in the serial number attribute, and the
# key usages the test cases ask for.
#
# The root certificates and the OFTP2 authorities of A and B are the ones to send to Odette for the test list;
# the authority of CB06 stays outside it on purpose, and CB05 is issued by the root directly - both are meant to
# be refused (test cases 7.2 and 7.3).
#
# Usage:
#   ./odette-test-pki.sh [options]
#   ./odette-test-pki.sh --revoke CA12          revokes one certificate and writes the list again (test case 7.1)
#
set -euo pipefail

OUT="./odette-test-pki"
CRL_URL="http://localhost:8080/crl"
P12_PASSWORD=""
HOST_A="oftp-a.example.com"
HOST_B="oftp-b.example.com"
ID_A="O0013000000TESTA"
ID_B="O0013000000TESTB"
KEY_BITS=3072
REVOKE=""

usage() {
    sed -n '2,/^set -euo/p' "$0" | sed 's/^# \{0,1\}//; $d'
    cat <<EOF
Options:
  -o, --out DIR         where to write everything (default $OUT)
  -u, --crl-url URL     base address the revocation lists are published under (default $CRL_URL)
  -p, --password PASS   password of the PKCS#12 bundles (default: none)
  -a, --host-a HOST     host name of company A (default $HOST_A)
  -b, --host-b HOST     host name of company B (default $HOST_B)
      --id-a PREFIX     Odette ID of company A; the stations A0 - A3 add 0 - 3 (default $ID_A)
      --id-b ID         Odette ID of company B (default $ID_B)
  -k, --key-bits BITS   RSA key size, 2048, 3072 or 4096 (default $KEY_BITS)
  -r, --revoke NAME     revoke this certificate (e.g. CA12) instead of creating the PKI
  -h, --help            this text
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        -o|--out) OUT="$2"; shift 2 ;;
        -u|--crl-url) CRL_URL="$2"; shift 2 ;;
        -p|--password) P12_PASSWORD="$2"; shift 2 ;;
        -a|--host-a) HOST_A="$2"; shift 2 ;;
        -b|--host-b) HOST_B="$2"; shift 2 ;;
        --id-a) ID_A="$2"; shift 2 ;;
        --id-b) ID_B="$2"; shift 2 ;;
        -k|--key-bits) KEY_BITS="$2"; shift 2 ;;
        -r|--revoke) REVOKE="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option $1" >&2; usage >&2; exit 2 ;;
    esac
done

command -v openssl >/dev/null || { echo "openssl is not installed." >&2; exit 1; }

# The configuration of an authority takes the subject alternative name from the environment; OpenSSL reads the
# whole file also for a revocation list, so the variable always has a value.
export SAN="DNS:localhost"

# One directory per certification authority: the OpenSSL database, the key, the certificate and the list.
ca_dir() { echo "$OUT/ca/$1"; }

ca_config() {
    local name="$1" dir crl
    dir="$(ca_dir "$name")"
    crl="$CRL_URL/$name.crl"
    cat > "$dir/openssl.cnf" <<EOF
[ ca ]
default_ca = authority

[ authority ]
dir             = $dir
database        = \$dir/index.txt
serial          = \$dir/serial
crlnumber       = \$dir/crlnumber
new_certs_dir   = \$dir/issued
certificate     = \$dir/ca.pem
private_key     = \$dir/ca.key
default_md      = sha256
default_days    = 1095
default_crl_days= 7
policy          = anything
email_in_dn     = no
rand_serial     = yes
unique_subject  = no
copy_extensions = none

[ anything ]
countryName             = optional
stateOrProvinceName     = optional
organizationName        = optional
organizationalUnitName  = optional
commonName              = supplied
serialNumber            = optional
emailAddress            = optional

[ req ]
distinguished_name = req_dn
prompt             = no

[ req_dn ]
CN = placeholder

[ ca_extensions ]
basicConstraints       = critical,CA:TRUE
keyUsage               = critical,keyCertSign,cRLSign
subjectKeyIdentifier   = hash
authorityKeyIdentifier = keyid:always
crlDistributionPoints  = URI:$crl

# The end entity profiles of the test cases. Every certificate carries the address of the revocation list of
# its authority, which the tests read (OP08 2.6).
[ tls_and_files ]
basicConstraints       = critical,CA:FALSE
keyUsage               = critical,digitalSignature,keyEncipherment
extendedKeyUsage       = serverAuth,clientAuth
subjectKeyIdentifier   = hash
authorityKeyIdentifier = keyid,issuer
crlDistributionPoints  = URI:$crl
subjectAltName         = \${ENV::SAN}

[ signing_only ]
basicConstraints       = critical,CA:FALSE
keyUsage               = critical,digitalSignature
subjectKeyIdentifier   = hash
authorityKeyIdentifier = keyid,issuer
crlDistributionPoints  = URI:$crl
subjectAltName         = \${ENV::SAN}

[ encryption_only ]
basicConstraints       = critical,CA:FALSE
keyUsage               = critical,keyEncipherment
subjectKeyIdentifier   = hash
authorityKeyIdentifier = keyid,issuer
crlDistributionPoints  = URI:$crl
subjectAltName         = \${ENV::SAN}
EOF
}

new_ca() {
    local name="$1" subject="$2" issuer="${3:-}" dir
    dir="$(ca_dir "$name")"
    mkdir -p "$dir/issued"
    : > "$dir/index.txt"
    echo 1000 > "$dir/crlnumber"
    openssl genrsa -out "$dir/ca.key" "$KEY_BITS" 2>/dev/null
    ca_config "$name"

    if [ -z "$issuer" ]; then
        SAN="DNS:localhost" openssl req -new -x509 -key "$dir/ca.key" -out "$dir/ca.pem" -days 7300 -sha256 \
            -subj "$subject" -config "$dir/openssl.cnf" -extensions ca_extensions 2>/dev/null
    else
        local parent
        parent="$(ca_dir "$issuer")"
        SAN="DNS:localhost" openssl req -new -key "$dir/ca.key" -out "$dir/ca.csr" -sha256 \
            -subj "$subject" -config "$dir/openssl.cnf" 2>/dev/null
        SAN="DNS:localhost" openssl ca -batch -config "$parent/openssl.cnf" -extensions ca_extensions \
            -days 3650 -in "$dir/ca.csr" -out "$dir/ca.pem" -notext 2>/dev/null
        rm -f "$dir/ca.csr"
    fi

    echo "  authority $name: $subject"
}

# A certificate of a station: name, issuing authority, subject, profile, validity in days and the host name for
# the subject alternative name.
new_certificate() {
    local name="$1" authority="$2" subject="$3" profile="$4" days="$5" host="$6" dir out
    dir="$(ca_dir "$authority")"
    out="$OUT/$(dirname "$name")"
    mkdir -p "$out"

    openssl genrsa -out "$OUT/$name.key" "$KEY_BITS" 2>/dev/null
    SAN="DNS:$host" openssl req -new -key "$OUT/$name.key" -out "$OUT/$name.csr" -sha256 \
        -subj "$subject" -config "$dir/openssl.cnf" 2>/dev/null
    SAN="DNS:$host" openssl ca -batch -config "$dir/openssl.cnf" -extensions "$profile" -days "$days" \
        -in "$OUT/$name.csr" -out "$OUT/$name.pem" -notext 2>/dev/null
    rm -f "$OUT/$name.csr"

    # The bundle to import into the OFTP2 software, and the certificate alone for the partner.
    openssl pkcs12 -export -out "$OUT/$name.p12" -inkey "$OUT/$name.key" -in "$OUT/$name.pem" \
        -certfile "$dir/ca.pem" -name "$(basename "$name")" -passout "pass:$P12_PASSWORD" 2>/dev/null
    openssl x509 -in "$OUT/$name.pem" -outform DER -out "$OUT/$name.cer"

    echo "  $(basename "$name"): $subject"
}

write_crl() {
    local name="$1" dir
    dir="$(ca_dir "$name")"
    mkdir -p "$OUT/crl"
    openssl ca -config "$dir/openssl.cnf" -gencrl -out "$OUT/crl/$name.crl.pem" 2>/dev/null
    openssl crl -in "$OUT/crl/$name.crl.pem" -outform DER -out "$OUT/crl/$name.crl"
    rm -f "$OUT/crl/$name.crl.pem"
}

if [ -n "$REVOKE" ]; then
    certificate="$(find "$OUT" -name "$REVOKE.pem" -not -path "*/ca/*" | head -1)"
    [ -n "$certificate" ] || { echo "Certificate $REVOKE not found under $OUT." >&2; exit 1; }
    issuer="$(basename "$(dirname "$certificate")")"
    authority="$([ "$issuer" = "a" ] && echo oftp2-ca-a || echo oftp2-ca-b)"
    openssl ca -config "$(ca_dir "$authority")/openssl.cnf" -revoke "$certificate" 2>/dev/null
    write_crl "$authority"
    echo "$REVOKE is revoked, $OUT/crl/$authority.crl is written again."
    echo "Publish it under $CRL_URL/$authority.crl and wait for the partner to read it."
    exit 0
fi

[ -e "$OUT" ] && { echo "$OUT already exists, remove it or choose another directory with --out." >&2; exit 1; }
mkdir -p "$OUT"

A0="$ID_A"0; A1="$ID_A"1; A2="$ID_A"2; A3="$ID_A"3

echo "Certification authorities"
new_ca "root-ca-a" "/C=CZ/O=OFTP2 Test Company A/CN=OFTP2 Test Company A Root CA"
new_ca "oftp2-ca-a" "/C=CZ/O=OFTP2 Test Company A/CN=OFTP2 Test Company A OFTP2 CA" "root-ca-a"
new_ca "root-ca-b" "/C=CZ/O=OFTP2 Test Company B/CN=OFTP2 Test Company B Root CA"
new_ca "oftp2-ca-b" "/C=CZ/O=OFTP2 Test Company B/CN=OFTP2 Test Company B OFTP2 CA" "root-ca-b"
new_ca "outside-ca" "/C=CZ/O=OFTP2 Test Outsider/CN=Authority outside the Odette trust list"

# Subjects: the certificates of one station share a subject where the test cases require it, and differ where
# they require that (the logical identification data tells them apart).
S_A0="/C=CZ/O=OFTP2 Test Company A/CN=$HOST_A/serialNumber=$A0"
S_A0_NEW="/C=CZ/O=OFTP2 Test Company A/OU=Station A0/CN=$HOST_A/serialNumber=$A0"
S_A1_EERP="/C=CZ/O=OFTP2 Test Company A/OU=End responses A1/CN=eerp.$HOST_A/serialNumber=$A1"
S_A1_SIGN="/C=CZ/O=OFTP2 Test Company A/OU=File security A1/CN=files.$HOST_A/serialNumber=$A1"
S_A0_13="/C=CZ/O=OFTP2 Test Company A/OU=Station A0 replacement/CN=r13.$HOST_A/serialNumber=$A0"
S_A0_14="/C=CZ/O=OFTP2 Test Company A/OU=Station A0 replacement 2/CN=r14.$HOST_A/serialNumber=$A0"
S_B="/C=CZ/O=OFTP2 Test Company B/CN=$HOST_B/serialNumber=$ID_B"

echo "Certificates of company A"
new_certificate "a/CA01" oftp2-ca-a "$S_A0"     tls_and_files    730  "$HOST_A"
new_certificate "a/CA02" oftp2-ca-a "$S_A0"     tls_and_files    1460 "$HOST_A"   # roll-over of CA01, later expiry
new_certificate "a/CA03" oftp2-ca-a "$S_A0"     tls_and_files    730  "$HOST_A"   # replacement for CA01
new_certificate "a/CA04" oftp2-ca-a "$S_A0_NEW" tls_and_files    730  "$HOST_A"
new_certificate "a/CA05" oftp2-ca-a "$S_A0_NEW" signing_only     730  "$HOST_A"   # invoices from A3
new_certificate "a/CA06" oftp2-ca-a "$S_A0_NEW" encryption_only  730  "$HOST_A"   # orders to A3
new_certificate "a/CA07" oftp2-ca-a "$S_A0_NEW" tls_and_files    1460 "$HOST_A"   # roll-over of CA04
new_certificate "a/CA08" oftp2-ca-a "$S_A0_NEW" tls_and_files    730  "$HOST_A"   # replacement for CA04
new_certificate "a/CA09" oftp2-ca-a "$S_A0_NEW" encryption_only  730  "$HOST_A"   # authentication of A0
new_certificate "a/CA10" oftp2-ca-a "$S_A1_EERP" signing_only    730  "eerp.$HOST_A"
new_certificate "a/CA11" oftp2-ca-a "$S_A1_SIGN" signing_only    730  "files.$HOST_A"
new_certificate "a/CA12" oftp2-ca-a "$S_A1_EERP" encryption_only 730  "eerp.$HOST_A"
new_certificate "a/CA13" oftp2-ca-a "$S_A0_13"  tls_and_files    730  "$HOST_A"   # replacement for CA04, other subject
new_certificate "a/CA14" oftp2-ca-a "$S_A0_14"  tls_and_files    730  "$HOST_A"   # replacement for CA13

echo "Certificates of company B"
new_certificate "b/CB01" oftp2-ca-b "$S_B" tls_and_files 730 "$HOST_B"
new_certificate "b/CB04" oftp2-ca-b "$S_B" tls_and_files 730 "$HOST_B"            # replacement for CB01
new_certificate "b/CB05" root-ca-b  "$S_B" tls_and_files 730 "$HOST_B"            # issued by the root: must be refused
new_certificate "b/CB06" outside-ca "$S_B" tls_and_files 730 "$HOST_B"            # authority outside the TSL

echo "Revocation lists"
for authority in root-ca-a oftp2-ca-a root-ca-b oftp2-ca-b outside-ca; do
    write_crl "$authority"
    echo "  $OUT/crl/$authority.crl"
done

cat <<EOF

Done. What to do with it:

1. Publish $OUT/crl over HTTP under $CRL_URL, reachable from the internet for the test partners.
2. Send these to Odette for the test list (TSL_Test.xml):
     $(ca_dir root-ca-a)/ca.pem   $(ca_dir oftp2-ca-a)/ca.pem
     $(ca_dir root-ca-b)/ca.pem   $(ca_dir oftp2-ca-b)/ca.pem
   The authority $(ca_dir outside-ca)/ca.pem stays outside it, test case 7.2 needs that.
3. Import the PKCS#12 bundles (*.p12) of your station on the Certificates page and give the partner the
   certificates alone (*.cer).
4. Revoke a certificate for test case 7.1 with:  $0 --revoke CA12 --out $OUT
EOF
