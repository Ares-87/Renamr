# Fake HTTPS proxy for the README screenshots: answers TMDb and TVmaze with canned data, so the preview shows
# real-looking matches without API keys or network. Writes its CA to ca.pem (in MOCK_DIR or next to this file).
import json, os, socket, ssl, sys, threading, tempfile, datetime
from urllib.parse import urlparse, parse_qs
from cryptography import x509
from cryptography.x509.oid import NameOID
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa

HERE = os.environ.get("MOCK_DIR") or os.path.dirname(os.path.abspath(__file__))
def mk(name, issuer=None, ikey=None, ca=False, san=None):
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    subj = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, name)])
    now = datetime.datetime.now(datetime.timezone.utc)
    b = (x509.CertificateBuilder().subject_name(subj).issuer_name(issuer or subj).public_key(key.public_key())
         .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(days=1))
         .not_valid_after(now + datetime.timedelta(days=30))
         .add_extension(x509.BasicConstraints(ca=ca, path_length=None), critical=True))
    if san: b = b.add_extension(x509.SubjectAlternativeName([x509.DNSName(san)]), critical=False)
    cert = b.sign(ikey or key, hashes.SHA256())
    return key, cert
cakey, cacert = mk("Renamr screenshot CA", ca=True)
open(os.path.join(HERE, "ca.pem"), "wb").write(cacert.public_bytes(serialization.Encoding.PEM))
ctxs = {}
def ctx_for(host):
    if host not in ctxs:
        k, c = mk(host, cacert.subject, cakey, san=host)
        d = tempfile.mkdtemp()
        cf, kf = os.path.join(d, "c.pem"), os.path.join(d, "k.pem")
        open(cf, "wb").write(c.public_bytes(serialization.Encoding.PEM))
        open(kf, "wb").write(k.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.TraditionalOpenSSL, serialization.NoEncryption()))
        cx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); cx.load_cert_chain(cf, kf); cx.set_alpn_protocols(["http/1.1"])
        ctxs[host] = cx
    return ctxs[host]

G = {"Drama": 18, "History": 36, "Science Fiction": 878, "Adventure": 12, "Action": 28, "Thriller": 53, "Horror": 27, "Comedy": 35, "Fantasy": 14, "Family": 10751, "Romance": 10749, "Mystery": 9648}
MOVIES = {
 "backrooms": (1286000, "Backrooms", "2026-05-27", None, ["Horror", "Science Fiction"], "A therapist searching for a missing patient finds a doorway to an endless maze of empty yellow rooms."),
 "black bag": (1233575, "Black Bag", "2025-03-12", None, ["Thriller", "Drama", "Mystery"], "When intelligence agent Kathryn is suspected of betraying the nation, her husband must choose between loyalty to his marriage and his country."),
 "bugonia": (701387, "Bugonia", "2025-10-23", None, ["Comedy", "Science Fiction", "Thriller"], "Two conspiracy-obsessed young men kidnap the powerful CEO of a major company, convinced that she is an alien intent on destroying Earth."),
 "death of a unicorn": (1153714, "Death of a Unicorn", "2025-03-17", None, ["Comedy", "Fantasy", "Horror"], "A father and daughter accidentally hit and kill a unicorn while en route to a weekend retreat."),
 "bram stoker s dracula": (6114, "Bram Stoker's Dracula", "1992-11-13", None, ["Romance", "Horror"], "In 19th-century England, Count Dracula travels to London and meets Mina Harker, a young woman who appears as the reincarnation of his lost love."),
 "how to train your dragon": (1087192, "How to Train Your Dragon", "2025-06-06", None, ["Fantasy", "Family", "Action"], "On the rugged isle of Berk, a young Viking named Hiccup defies centuries of tradition when he befriends a dragon."),
}
ALIASES = {"dracula di bram stoker": "bram stoker s dracula", "dragon trainer": "how to train your dragon"}
SHOWS = {
 "silo": (44933, "Silo", "2023-05-05", "tt14688458", ["Drama", "Science-Fiction"], {(1,1):("Freedom Day","2023-05-05"),(1,2):("Holston's Pick","2023-05-05"),(1,3):("Machines","2023-05-12"),(1,4):("Truth","2023-05-19")}),
 "severance": (44782, "Severance", "2022-02-18", "tt11280740", ["Drama", "Science-Fiction", "Thriller"], {(1,1):("Good News About Hell","2022-02-18"),(1,2):("Half Loop","2022-02-18"),(2,1):("Hello, Ms. Cobel","2025-01-17")}),
}
def norm(s): return " ".join("".join(ch.lower() if ch.isalnum() else " " for ch in s).split())
def movie_json(v, full=False):
    i, t, d, imdb, g, o = v
    m = {"id": i, "title": t, "original_title": t, "release_date": d, "overview": o, "genre_ids": [G.get(x, 18) for x in g], "adult": False, "popularity": 50.0, "vote_average": 8.0, "vote_count": 1000, "original_language": "en"}
    if full: m.update({"imdb_id": imdb, "genres": [{"id": G.get(x, 18), "name": x} for x in g], "runtime": 150, "status": "Released"})
    return m

def route(host, path):
    u = urlparse(path); q = {k: v[0] for k, v in parse_qs(u.query).items()}; p = u.path
    if "themoviedb" in host:
        if p.endswith("/search/movie"):
            n = norm(q.get("query", "")); n = next((v for a, v in ALIASES.items() if a in n), n); res = [movie_json(v) for k, v in MOVIES.items() if k == n or k in n or n in k]
            return 200, {"page": 1, "results": res, "total_pages": 1, "total_results": len(res)}
        if p.endswith("/search/tv"):
            return 200, {"page": 1, "results": [], "total_pages": 0, "total_results": 0}
        if "/movie/" in p:
            mid = int(p.rstrip("/").split("/")[-1])
            for v in MOVIES.values():
                if v[0] == mid: return 200, movie_json(v, True)
    if "tvmaze" in host:
        if p == "/search/shows":
            n = norm(q.get("q", "")); res = []
            for k, (i, name, prem, imdb, g, eps) in SHOWS.items():
                if k == n or k in n:
                    res.append({"score": 0.9, "show": {"id": i, "name": name, "premiered": prem, "genres": g, "externals": {"imdb": imdb}}})
            return 200, res
        parts = p.strip("/").split("/")
        if len(parts) == 3 and parts[0] == "shows":
            sid = int(parts[1])
            show = next(v for v in SHOWS.values() if v[0] == sid)
            if parts[2] == "akas": return 200, []
            if parts[2] == "episodebynumber":
                e = show[5].get((int(q["season"]), int(q["number"])))
                if e: return 200, {"name": e[0], "season": int(q["season"]), "number": int(q["number"]), "airdate": e[1], "summary": None}
    print("UNHANDLED", host, path, file=sys.stderr, flush=True)
    return 404, {"status_code": 34, "status_message": "not found"}

def serve_tls(conn, host):
    tls = ctx_for(host).wrap_socket(conn, server_side=True)
    f = tls.makefile("rb")
    while True:
        line = f.readline()
        if not line: break
        method, path, _ = line.decode().split(" ", 2)
        hdrs = {}
        while True:
            h = f.readline().decode()
            if h in ("\r\n", "\n", ""): break
            k, v = h.split(":", 1); hdrs[k.strip().lower()] = v.strip()
        if int(hdrs.get("content-length", 0)): f.read(int(hdrs["content-length"]))
        code, body = route(host, path)
        print(code, host, path, file=sys.stderr, flush=True)
        data = json.dumps(body).encode()
        tls.sendall(f"HTTP/1.1 {code} {'OK' if code == 200 else 'Not Found'}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {len(data)}\r\n\r\n".encode() + data)

def handle(conn):
    try:
        buf = b""
        while b"\r\n\r\n" not in buf:
            chunk = conn.recv(4096)
            if not chunk: return
            buf += chunk
        first = buf.split(b"\r\n")[0].decode()
        method, target, _ = first.split(" ")
        if method != "CONNECT": conn.close(); return
        host = target.split(":")[0]
        if not any(h in host for h in ("themoviedb", "tvmaze")):
            print("REFUSED", host, file=sys.stderr, flush=True)
            conn.sendall(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"); conn.close(); return
        conn.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
        serve_tls(conn, host)
    except Exception as e:
        print("ERR", repr(e), file=sys.stderr, flush=True)
    finally:
        try: conn.close()
        except Exception: pass

s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(("127.0.0.1", 8899)); s.listen(50)
print("ready", flush=True)
while True:
    c, _ = s.accept(); threading.Thread(target=handle, args=(c,), daemon=True).start()
