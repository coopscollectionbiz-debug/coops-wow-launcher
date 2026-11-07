"""Throttled loopback feed for launcher smoke tests."""
import functools
import http.server
import pathlib
import sys
import time


class Handler(http.server.SimpleHTTPRequestHandler):
    def copyfile(self, source, output):
        if self.path.endswith((".exe", ".zip")):
            for _ in range(4):
                chunk = source.read(128 * 1024)
                if not chunk:
                    return
                output.write(chunk)
                output.flush()
                time.sleep(0.25)
        super().copyfile(source, output)


with http.server.ThreadingHTTPServer(
    ("127.0.0.1", 0), functools.partial(Handler, directory=sys.argv[1])
) as server:
    pathlib.Path(sys.argv[2]).write_text(str(server.server_port), encoding="ascii")
    server.serve_forever()
