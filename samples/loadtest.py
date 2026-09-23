#!/usr/bin/env python3
"""Load test of an Oftp4Net installation: many partners send files at the same time.

Every partner is a self loop - an identity, a partner and a listener that share one Odette code - so one instance
plays both sides of every session. The throughput is therefore pessimistic (the same machine runs the client, the
server and TLS twice), but what a production load does show up all the same: sessions in parallel, the memory of
the file security, deadlocks, leaks and mix-ups between partners.

It writes into the database of the server, so run it against a test installation, never against a production one.

    samples/loadtest.py setup --partners 60 --size-kb 1024     # partners, listeners and the payload
    <restart the server so that it opens the listeners>
    samples/loadtest.py run --partners 60 --files 5            # queue the files and measure
    samples/loadtest.py cleanup                                # remove everything of the test again

Everything it creates has the Odette code O0013LOAD… and the name "Load …", so cleanup finds it.
"""
import argparse
import os
import shlex
import shutil
import subprocess
import sys
import time

PREFIX = "O0013LOAD"
DEFAULT_PSQL = "docker exec -e PGPASSWORD=root postgres psql -U root -d oftp4net-dev"


class Database:
    def __init__(self, command):
        self.command = shlex.split(command)

    def __call__(self, sql, quiet=True):
        result = subprocess.run(self.command + ["-At", "-c", sql], capture_output=True, text=True)
        if result.returncode != 0 and not quiet:
            print(result.stderr, file=sys.stderr)
            sys.exit(1)
        return result.stdout.strip()


def payload_path(directory, size_kb):
    return os.path.join(directory, f"payload-{size_kb}k.bin")


def cleanup(db, files, received):
    db(f"""
        delete from "SendQueueItems" where "PartnerId" in (select "Id" from "Partners" where "SSID" like '{PREFIX}%');
        delete from "ReceivedFiles" where "PartnerId" in (select "Id" from "Partners" where "SSID" like '{PREFIX}%');
        delete from "Listeners" where "Name" like 'Load %';
        delete from "Partners" where "SSID" like '{PREFIX}%';
        delete from "Identities" where "SSID" like '{PREFIX}%';
        delete from "GlobalSettings" where "Name" in ('MaxParallelSessions', 'SendIntervalSeconds');
    """)
    shutil.rmtree(files, ignore_errors=True)
    if received and os.path.isdir(received):
        for name in os.listdir(received):
            if name.startswith(PREFIX):
                shutil.rmtree(os.path.join(received, name), ignore_errors=True)


def setup(db, args):
    os.makedirs(args.files_directory, exist_ok=True)
    with open(payload_path(args.files_directory, args.size_kb), "wb") as file:
        file.write(os.urandom(args.size_kb * 1024))

    certificate = db(f"""select "Id" from "Certificate" where "HasPrivateKey" order by "Id" limit 1""")
    if not certificate:
        print("No certificate with a private key is stored; the listeners need one for TLS.", file=sys.stderr)
        sys.exit(1)

    statements = [
        f"""insert into "GlobalSettings" ("Name","Json") values ('MaxParallelSessions','{args.parallel}')
            on conflict ("Name") do update set "Json" = excluded."Json";""",
        f"""insert into "GlobalSettings" ("Name","Json") values ('SendIntervalSeconds','10')
            on conflict ("Name") do update set "Json" = excluded."Json";""",
    ]
    if args.security:
        statements.append(f"""insert into "GlobalSettings" ("Name","Json") values ('FileSecurityCertificateId','{certificate}')
            on conflict ("Name") do update set "Json" = excluded."Json";""")

    secured = "true" if args.security else "false"
    for index in range(args.partners):
        code = f"{PREFIX}{index:04d}"
        statements.append(f"""
            with identity as (
                insert into "Identities" ("Name","Description","SSID","SFID","Password","Certificates")
                values ('Load {index}', 'load test', '{code}', '{code}', 'LOAD', '[]'::jsonb) returning "Id"
            ), partner as (
                insert into "Partners" ("Name","Description","SSID","SFID","Password","Host","Port","UseTls","Tls",
                    "TrustedCertificateId","OutgoingEncoding","ConvertIncomingEbcdicToAnsi","AnsiCodePage","EbcdicCodePage",
                    "ProtocolLevel","BufferCompression","Restart","CompressFiles","SignFiles","EncryptFiles",
                    "SecureAuthentication","RequestSignedEndResponse","FileCipherSuite","SecurityCertificateId",
                    "RequireSignedFiles","RequireEncryptedFiles","RequireCompressedFiles","PdxVersion",
                    "Contacts","SubStations","InboundDsnPatterns","OutboundDsnPatterns","Certificates")
                values ('Load {index}', 'load test', '{code}', '{code}', 'LOAD', '{args.host}', {args.port_base + index},
                    true, 3072, {certificate}, 'ANSI', false, 1252, 500, 5, false, false, false,
                    {secured}, {secured}, false, false, '02', {certificate if args.security else 'null'},
                    false, false, false, '1.2',
                    '[]'::jsonb, '[]'::jsonb, '[]'::jsonb, '[]'::jsonb, '[]'::jsonb) returning "Id"
            )
            insert into "Listeners" ("Name","Enabled","ListenIPAddress","Port","UseTls","Tls","CertificateId",
                "IdentityId","RequireClientCertificate")
            select 'Load {index}', true, '{args.host}', {args.port_base + index}, true, 3072, {certificate},
                   identity."Id", false from identity;
        """)

    db("".join(statements), quiet=False)
    print(f"prepared: {args.partners} partners on ports {args.port_base}-{args.port_base + args.partners - 1}, "
          f"{args.size_kb} kB payload, {args.parallel} sessions in parallel, "
          f"file security {'on' if args.security else 'off'}")
    print("restart the server so that it opens the listeners, then run: loadtest.py run")


def queue(db, args):
    payload = payload_path(args.files_directory, args.size_kb)
    if not os.path.exists(payload):
        print(f"{payload} does not exist; run setup first.", file=sys.stderr)
        sys.exit(1)

    values = []
    for index in range(args.partners):
        code = f"{PREFIX}{index:04d}"
        for number in range(args.files):
            # The same virtual file names for every partner on purpose: that is what a real installation looks like.
            values.append(f"""((select "Id" from "Partners" where "SSID"='{code}'),
                (select "Id" from "Identities" where "SSID"='{code}'),
                'LOAD{number:04d}', '{payload}', 0, now(), 0, now(), 'U', 0, false, 0)""")

    db(f"""insert into "SendQueueItems" ("PartnerId","IdentityId","VirtualFileName","FilePath","Status","Created",
           "RetryCount","NextRetry","Format","MaxRecordSize","SignedResponseRequested","RestartPosition")
           values {','.join(values)};""", quiet=False)


def resident_megabytes(pid):
    if not pid:
        return 0
    out = subprocess.run(["ps", "-o", "rss=", "-p", str(pid)], capture_output=True, text=True).stdout.strip()
    return int(out) / 1024 if out else 0


def server_pid(pattern):
    out = subprocess.run(["pgrep", "-f", pattern], capture_output=True, text=True).stdout.split()
    return int(out[0]) if out else None


def run(db, args):
    pid = server_pid(args.process)
    if not pid:
        print(f"No process matches '{args.process}', the memory is not measured.")

    queue(db, args)
    start = time.time()
    peak = 0.0
    samples = []
    pending = args.partners * args.files

    while time.time() - start < args.timeout:
        # NEW, ERROR and SENT: a file counts as done when its end response has arrived.
        pending = int(db(f"""select count(*) from "SendQueueItems" i join "Partners" p on p."Id" = i."PartnerId"
                            where p."SSID" like '{PREFIX}%' and i."Status" in (0, 42, 999)""") or 0)
        resident = resident_megabytes(pid)
        peak = max(peak, resident)
        samples.append((round(time.time() - start, 1), pending, round(resident)))
        if pending == 0:
            break
        time.sleep(2)

    duration = time.time() - start
    total = args.partners * args.files
    delivered = int(db(f"""select count(*) from "SendQueueItems" i join "Partners" p on p."Id" = i."PartnerId"
                          where p."SSID" like '{PREFIX}%' and i."Status" = 1000""") or 0)
    failed = int(db(f"""select count(*) from "SendQueueItems" i join "Partners" p on p."Id" = i."PartnerId"
                       where p."SSID" like '{PREFIX}%' and i."Status" in (42, 43, 44)""") or 0)
    received = int(db(f"""select count(*) from "ReceivedFiles" r join "Partners" p on p."Id" = r."PartnerId"
                         where p."SSID" like '{PREFIX}%'""") or 0)
    megabytes = total * args.size_kb / 1024

    print()
    print(f"duration   : {duration:.0f} s" + ("" if pending == 0 else f" (stopped with {pending} unfinished)"))
    print(f"delivered  : {delivered} / {total}   received: {received}   failed: {failed}")
    print(f"throughput : {total / duration:.1f} files/s, {megabytes / duration:.1f} MB/s "
          f"(both directions in one process)")
    print(f"peak memory: {peak:.0f} MB")
    print("samples (s, unfinished, MB):", samples[:: max(1, len(samples) // 10)])


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("phase", choices=["setup", "run", "cleanup"])
    parser.add_argument("--partners", type=int, default=60)
    parser.add_argument("--files", type=int, default=5, help="files per partner (run)")
    parser.add_argument("--size-kb", type=int, default=1024)
    parser.add_argument("--parallel", type=int, default=16, help="sessions the send service runs at the same time")
    parser.add_argument("--security", action="store_true", help="sign and encrypt the files")
    parser.add_argument("--host", default="127.0.0.1", help="address the listeners open and the partners call")
    parser.add_argument("--port-base", type=int, default=16700)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--psql", default=DEFAULT_PSQL, help=f"how to reach the database (default: {DEFAULT_PSQL})")
    parser.add_argument("--files-directory", default=os.path.join(os.path.expanduser("~"), ".oftp4net-loadtest"))
    parser.add_argument("--received-directory", help="receive directory, so that cleanup removes what arrived")
    parser.add_argument("--process", default="Oftp4Net.Server", help="process pattern for the memory measurement")
    args = parser.parse_args()

    db = Database(args.psql)
    if args.phase == "cleanup":
        cleanup(db, args.files_directory, args.received_directory)
        print("everything of the load test is removed")
    elif args.phase == "setup":
        cleanup(db, args.files_directory, args.received_directory)
        setup(db, args)
    else:
        run(db, args)


if __name__ == "__main__":
    main()
