"""Forward POSTs to the model server and log each request body, to see exactly what a client sends."""
import http.server, json, pathlib, urllib.request

TARGET = "http://localhost:12434"
LOG = pathlib.Path(__file__).with_name("proxy-requests.log")

class H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        with LOG.open("ab") as f:
            f.write(b"PATH " + self.path.encode() + b"\n" + body + b"\n\n")
        req = urllib.request.Request(TARGET + self.path, body, {"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=300) as r:
            data = r.read()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    def log_message(self, *a):
        pass

http.server.HTTPServer(("127.0.0.1", 12435), H).serve_forever()
