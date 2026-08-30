#!/usr/bin/env python3
"""CmmTuneWeb — tune the emitter retune by ear, with your real music, without the game.

    python tools/CmmTuneWeb/serve.py
    ...then open http://localhost:8787

WHY IT IS LOCAL AND NOT A HOSTED PAGE
A browser cannot read J:\\7D2D Custom Music. That is a hard sandbox boundary, not a
configuration -- so anything that plays your actual station folders has to be served by a
process on this machine. This is that process, stdlib only, read-only, localhost only.

WHAT IS HONEST ABOUT IT, AND WHAT IS NOT
  EXACT   the SEQUENCE and the TIMINGS -- leave, static floor, handover, the mandatory
          silence as a floor from the handover, arrive. Every bug of the last two days has
          been in that sequencing, and this reproduces it with your real tracks.
  REAL    the static itself is a WAV rendered by the shipping receiver
          (tools/CmmEmitLab, `--pass hiss --wav`), NOT synthesised in the browser. The one
          rule the offline radio simulator exists to enforce is that we never tune a
          reimplementation, and a Web Audio noise generator would be exactly that.
  NOT     the static's LEVEL CURVE. In game the receiver's `signal` is not a volume -- it
          buries a station through a knee and a 5 dB floor. Here the sample is faded with a
          plain gain. So trust this for WHEN things happen, and CmmEmitLab or the game for
          HOW LOUD.
  ABSENT  occlusion, distance, the speaker's cone colouring, reverb. All of that is
          consumer-side native spatialisation and exists only in the game.
"""
import http.server, socketserver, json, os, re, urllib.parse, mimetypes, sys

ROOT   = os.path.dirname(os.path.abspath(__file__))
MUSIC  = os.environ.get("CMM_MUSIC", r"J:\7D2D Custom Music")
RADIO  = os.path.join(MUSIC, "radio")
PORT   = int(os.environ.get("CMM_PORT", "8787"))
AUDIO  = (".mp3", ".m4a", ".flac", ".ogg", ".opus", ".wav", ".aiff", ".aif")

mimetypes.add_type("audio/mp4", ".m4a")
mimetypes.add_type("audio/flac", ".flac")
mimetypes.add_type("audio/ogg", ".opus")


def read_m3u(path):
    """Absolute track paths out of an .m3u, the way PlaylistSource does.

    These playlists carry a UTF-8 BOM, CRLF endings and #EXT lines, and their entries are
    absolute paths onto another drive entirely (H:\Music) rather than files sitting beside
    the playlist. A station folder therefore usually contains ONE .m3u and no audio at all,
    which is why listing audio files found nothing."""
    out = []
    try:
        with open(path, encoding="utf-8-sig", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                p = line if os.path.isabs(line) else os.path.join(os.path.dirname(path), line)
                if p.lower().endswith(AUDIO):
                    out.append(os.path.normpath(p))
    except OSError:
        pass
    return out


_INDEX = {}          # station -> [absolute path]; the ONLY paths this server will serve


def stations(refresh=False):
    """Subfolders of radio/ are stations — the same rule the mod uses. A station's tracks are
    the audio files inside it PLUS everything its playlists point at."""
    global _INDEX
    if _INDEX and not refresh:
        return [{"name": k, "tracks": [os.path.basename(p) for p in v]}
                for k, v in _INDEX.items()]
    _INDEX = {}
    if not os.path.isdir(RADIO):
        return []
    for name in sorted(os.listdir(RADIO)):
        d = os.path.join(RADIO, name)
        if not os.path.isdir(d):
            continue
        paths = []
        for f in sorted(os.listdir(d)):
            full = os.path.join(d, f)
            if f.lower().endswith(AUDIO):
                paths.append(full)
            elif f.lower().endswith(".m3u") or f.lower().endswith(".m3u8"):
                paths.extend(read_m3u(full))
        _INDEX[name] = paths
    return [{"name": k, "tracks": [os.path.basename(p) for p in v]}
            for k, v in _INDEX.items()]


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass                                   # quiet; this runs beside a game session

    def _send(self, code, ctype, body, extra=None):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def _file(self, path, ctype):
        """Serves a file, honouring Range so the browser can seek in a long track."""
        try:
            size = os.path.getsize(path)
        except OSError:
            return self._send(404, "text/plain", b"not found")
        rng = self.headers.get("Range")
        start, end = 0, size - 1
        if rng:
            m = re.match(r"bytes=(\d*)-(\d*)", rng)
            if m:
                if m.group(1):
                    start = int(m.group(1))
                if m.group(2):
                    end = int(m.group(2))
        end = min(end, size - 1)
        length = max(0, end - start + 1)
        with open(path, "rb") as f:
            f.seek(start)
            body = f.read(length)
        code = 206 if rng else 200
        extra = {"Accept-Ranges": "bytes"}
        if rng:
            extra["Content-Range"] = f"bytes {start}-{end}/{size}"
        self._send(code, ctype, body, extra)

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(u.query)

        if u.path in ("/", "/index.html"):
            return self._file(os.path.join(ROOT, "index.html"), "text/html; charset=utf-8")

        if u.path == "/static.wav":
            return self._file(os.path.join(ROOT, "static.wav"), "audio/wav")

        if u.path == "/stations":
            body = json.dumps({"root": RADIO, "stations": stations()}).encode()
            return self._send(200, "application/json", body)

        if u.path == "/track":
            # BY INDEX, NEVER BY PATH. Playlists point at absolute paths on another drive, so
            # a path parameter would mean serving arbitrary files. The index built at startup
            # is the complete allow-list, and nothing outside it can be named.
            st = (q.get("station") or [""])[0]
            try:
                i = int((q.get("i") or ["-1"])[0])
            except ValueError:
                i = -1
            stations()                               # ensure the index exists
            paths = _INDEX.get(st)
            if not paths or i < 0 or i >= len(paths):
                return self._send(404, "text/plain", b"not found")
            p = paths[i]
            if not os.path.isfile(p):
                return self._send(404, "text/plain", b"missing on disk")
            ctype = mimetypes.guess_type(p)[0] or "application/octet-stream"
            return self._file(p, ctype)

        self._send(404, "text/plain", b"not found")


if __name__ == "__main__":
    if not os.path.isdir(RADIO):
        sys.exit(f"no radio folder at {RADIO}  (set CMM_MUSIC to override)")
    if not os.path.isfile(os.path.join(ROOT, "static.wav")):
        print("NOTE: static.wav is missing — render it with")
        print("      tools/CmmEmitLab/emitlab.exe --pass hiss --wav tools/CmmTuneWeb/static.wav")
    n = len(stations())
    print(f"CmmTuneWeb  {RADIO}  ({n} stations)")
    print(f"            http://localhost:{PORT}")
    socketserver.ThreadingTCPServer.allow_reuse_address = True
    with socketserver.ThreadingTCPServer(("127.0.0.1", PORT), Handler) as srv:
        try:
            srv.serve_forever()
        except KeyboardInterrupt:
            pass
