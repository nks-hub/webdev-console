"""
NKS WebDev Console — Binary Catalog Mock API
=============================================

Mock implementation of the catalog backend that NKS WDC daemon will eventually
consume from a real production server. The JSON shape returned by every endpoint
here is the **API contract**: when the production backend is built it must return
identical structures.

Run:
    python server.py                  # binds 0.0.0.0:8765
    python server.py --port 9000      # custom port

Endpoints:
    GET  /healthz
    GET  /api/v1/catalog                      → full catalog (all apps)
    GET  /api/v1/catalog/apps                 → list of apps with summaries
    GET  /api/v1/catalog/apps/{app}           → single app + all releases
    GET  /api/v1/catalog/apps/{app}/releases  → releases array (filterable by ?os&arch&channel)
    GET  /api/v1/catalog/apps/{app}/releases/latest?os=windows&arch=x64
    GET  /api/v1/catalog/apps/{app}/releases/{version}

Stack: stdlib only — no FastAPI/Flask dependency. Pure http.server.
"""

import argparse
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, parse_qs

CATALOG_PATH = Path(__file__).parent / "catalog.json"


def load_catalog() -> dict:
    with CATALOG_PATH.open(encoding="utf-8") as fp:
        return json.load(fp)


def filter_release_downloads(release: dict, os_filter: str | None, arch_filter: str | None) -> dict | None:
    """Return a copy of release with downloads filtered to matching os/arch, or None if no match."""
    downloads = release.get("downloads", [])
    if os_filter:
        downloads = [d for d in downloads if d.get("os") == os_filter]
    if arch_filter:
        downloads = [d for d in downloads if d.get("arch") == arch_filter]
    if not downloads:
        return None
    return {**release, "downloads": downloads}


class Handler(BaseHTTPRequestHandler):
    catalog: dict = {}

    def log_message(self, format, *args):
        sys.stderr.write(f"[catalog-mock] {self.address_string()} - {format % args}\n")

    def _send(self, status: int, body: dict):
        payload = json.dumps(body, indent=2).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "public, max-age=300")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(payload)

    def _err(self, status: int, message: str):
        self._send(status, {"error": message, "status": status})

    def do_GET(self):  # noqa: N802
        url = urlparse(self.path)
        path = url.path.rstrip("/")
        params = {k: v[0] for k, v in parse_qs(url.query).items()}

        if path in ("", "/"):
            return self._send(200, {
                "service": "nks-wdc-binary-catalog-mock",
                "version": self.catalog.get("schema_version", "1.0"),
                "endpoints": [
                    "/healthz",
                    "/api/v1/catalog",
                    "/api/v1/catalog/apps",
                    "/api/v1/catalog/apps/{app}",
                    "/api/v1/catalog/apps/{app}/releases",
                    "/api/v1/catalog/apps/{app}/releases/latest",
                    "/api/v1/catalog/apps/{app}/releases/{version}",
                ],
            })

        if path == "/healthz":
            return self._send(200, {"ok": True, "apps": len(self.catalog.get("apps", {}))})

        if path == "/api/v1/catalog":
            return self._send(200, self.catalog)

        if path == "/api/v1/catalog/apps":
            apps = self.catalog.get("apps", {})
            summaries = [
                {
                    "name": app["name"],
                    "display_name": app["display_name"],
                    "category": app["category"],
                    "homepage": app.get("homepage"),
                    "license": app.get("license"),
                    "release_count": len(app.get("releases", [])),
                    "latest_version": app["releases"][0]["version"] if app.get("releases") else None,
                }
                for app in apps.values()
            ]
            return self._send(200, {"apps": summaries})

        # /api/v1/catalog/apps/{app}...
        if path.startswith("/api/v1/catalog/apps/"):
            tail = path[len("/api/v1/catalog/apps/"):]
            parts = tail.split("/")
            app_name = parts[0]
            app = self.catalog.get("apps", {}).get(app_name)
            if app is None:
                return self._err(404, f"app '{app_name}' not found")

            # /apps/{app}
            if len(parts) == 1:
                return self._send(200, app)

            # /apps/{app}/releases...
            if parts[1] == "releases":
                os_f = params.get("os")
                arch_f = params.get("arch")
                channel_f = params.get("channel")

                # /apps/{app}/releases
                if len(parts) == 2:
                    releases = app.get("releases", [])
                    if channel_f:
                        releases = [r for r in releases if r.get("channel") == channel_f]
                    filtered = []
                    for r in releases:
                        if os_f or arch_f:
                            f = filter_release_downloads(r, os_f, arch_f)
                            if f is not None:
                                filtered.append(f)
                        else:
                            filtered.append(r)
                    return self._send(200, {"app": app_name, "count": len(filtered), "releases": filtered})

                # /apps/{app}/releases/latest
                if parts[2] == "latest":
                    releases = app.get("releases", [])
                    major_minor = params.get("major_minor")
                    if major_minor:
                        releases = [r for r in releases if r.get("major_minor") == major_minor]
                    if channel_f:
                        releases = [r for r in releases if r.get("channel") == channel_f]
                    for r in releases:
                        f = filter_release_downloads(r, os_f, arch_f) if (os_f or arch_f) else r
                        if f is not None:
                            return self._send(200, f)
                    return self._err(404, f"no matching release for app '{app_name}'")

                # /apps/{app}/releases/{version}
                version = parts[2]
                for r in app.get("releases", []):
                    if r["version"] == version:
                        f = filter_release_downloads(r, os_f, arch_f) if (os_f or arch_f) else r
                        if f is None:
                            return self._err(404, f"no downloads for {os_f}/{arch_f}")
                        return self._send(200, f)
                return self._err(404, f"version '{version}' not found for app '{app_name}'")

            return self._err(404, f"unknown sub-resource '{parts[1]}'")

        return self._err(404, f"unknown path '{path}'")


def main() -> int:
    parser = argparse.ArgumentParser(description="NKS WDC binary catalog mock API")
    parser.add_argument("--host", default="127.0.0.1", help="bind host (default: 127.0.0.1)")
    parser.add_argument("--port", type=int, default=8765, help="bind port (default: 8765)")
    args = parser.parse_args()

    Handler.catalog = load_catalog()

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"[catalog-mock] serving {CATALOG_PATH.name} on http://{args.host}:{args.port}")
    print(f"[catalog-mock] {len(Handler.catalog.get('apps', {}))} apps loaded")
    print(f"[catalog-mock] try: curl http://{args.host}:{args.port}/api/v1/catalog/apps")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n[catalog-mock] shutting down")
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
