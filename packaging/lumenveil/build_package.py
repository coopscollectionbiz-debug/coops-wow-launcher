"""Build the optional Lumenveil graphics ZIP from an installed client.

Reads the file list in worgen-graphics.json, copies those exact files from the
source client (read-only), applies only the documented line removals, and writes
a deterministic ZIP plus a manifest and an unsigned feed payload.

Usage: python -I build_package.py --out <dir> [--source <client>] [--wow-sha256 <hex> ...]
"""
import argparse
import hashlib
import json
import os
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
FIXED_TIME = (2026, 11, 8, 0, 0, 0)
RELEASES = "https://github.com/coopscollectionbiz-debug/coops-wow-launcher/releases/download/"


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


LAYER_NAME = "VK_LAYER_coops_reshade"


def source_path(source: str, item) -> str:
    return item.get("from") or os.path.join(source, *item["path"].split("/"))


def read_source(path: str) -> bytes:
    if os.path.islink(path) or not os.path.isfile(path):
        sys.exit(f"missing or linked source file: {path}")
    with open(path, "rb") as f:
        return f.read()


def client_local_layer(data: bytes) -> bytes:
    """Rename the system ReShade layer so a client-local copy never collides with a system install."""
    manifest = json.loads(data.decode("utf-8-sig"))
    layer = manifest["layer"]
    if layer.get("name") != "VK_LAYER_reshade" or layer.get("library_path") != ".\\ReShade32.dll":
        sys.exit("unexpected ReShade32.json layout")
    layer["name"] = LAYER_NAME
    layer["disable_environment"] = {"DISABLE_" + LAYER_NAME: "1"}
    return (json.dumps(manifest, indent="\t") + "\n").replace("\n", "\r\n").encode("utf-8")


def adjust(data: bytes, prefixes, transform=None) -> bytes:
    if transform == "client-local-vulkan-layer":
        return client_local_layer(data)
    if transform:
        sys.exit(f"unknown transform {transform}")
    if not prefixes:
        return data
    lines = data.splitlines(keepends=True)
    kept = [l for l in lines if not any(l.lstrip().startswith(p.encode()) for p in prefixes)]
    if len(kept) == len(lines):
        sys.exit("expected adjustment line not found")
    return b"".join(kept)


def main():
    spec_path = os.path.join(HERE, "worgen-graphics.json")
    with open(spec_path, encoding="utf-8") as f:
        spec = json.load(f)
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--source", default=spec["defaultSource"])
    ap.add_argument("--wow-sha256", action="append", default=[])
    args = ap.parse_args()

    version = spec["version"]
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    zip_name = f"Lumenveil-{version}.zip"
    zip_path = os.path.join(out, zip_name)
    if os.path.exists(zip_path):
        sys.exit(f"refusing to overwrite {zip_path}")

    entries = []
    for item in sorted(spec["files"], key=lambda i: i["path"].lower()):
        rel = item["path"]
        src = source_path(args.source, item)
        original = read_source(src)
        packaged = adjust(original, item.get("removeLinesStartingWith"), item.get("transform"))
        entries.append({
            "path": rel,
            "from": src,
            "role": item["role"],
            "sourceSha256": sha256(original),
            "sourceSize": len(original),
            "packagedSha256": sha256(packaged),
            "packagedSize": len(packaged),
            "adjustment": item.get("adjustment"),
            "_bytes": packaged,
        })

    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for e in entries:
            info = zipfile.ZipInfo(e["path"], date_time=FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 0
            info.external_attr = 0x20  # FILE_ATTRIBUTE_ARCHIVE, no unix mode bits
            z.writestr(info, e.pop("_bytes"), compresslevel=9)

    # Re-read sources: the client must not have changed while packaging.
    for e in entries:
        if sha256(read_source(e["from"])) != e["sourceSha256"]:
            sys.exit(f"source changed during packaging: {e['path']}")

    with open(zip_path, "rb") as f:
        zip_bytes = f.read()
    package_sha = sha256(zip_bytes)
    files = [e["path"] for e in entries]

    manifest = {
        "name": spec["name"],
        "version": version,
        "source": args.source,
        "package": {"filename": zip_name, "size": len(zip_bytes), "sha256": package_sha},
        "files": entries,
        "excluded": spec["excluded"],
    }
    with open(os.path.join(out, "package-manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")

    metadata = {
        "channel": "lumenveil",
        "version": version,
        "filename": zip_name,
        "url": f"{RELEASES}lumenveil-v{version}/{zip_name}",
        "size": len(zip_bytes),
        "sha256": package_sha,
        "installationEnabled": True,
        "supportedBuilds": ["WoW 3.3.5a (12340), stock or St0ny-patched"],
        "compatibleWowSha256": [h.lower() for h in args.wow_sha256],
        "prerequisites": ["Vulkan graphics driver", "Start WoW with this launcher's Launch button for ReShade"],
        "files": files,
    }
    with open(os.path.join(out, "feed-payload.unsigned.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(metadata, f, indent=2)
        f.write("\n")

    with open(os.path.join(out, "SHA256SUMS.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(f"{package_sha}  {zip_name}\n")
        for e in entries:
            f.write(f"{e['packagedSha256']}  {e['path']}\n")

    print(f"{zip_name} {len(zip_bytes)} bytes sha256 {package_sha} ({len(entries)} files)")


if __name__ == "__main__":
    main()
