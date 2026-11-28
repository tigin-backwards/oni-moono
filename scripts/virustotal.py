#!/usr/bin/env python3
"""Has VirusTotal scan the Windows archive's own executables and writes their verdicts as a
Markdown table (to $GITHUB_STEP_SUMMARY when set, else stdout). Run by release.yml's VirusTotal
job; it never fails the release (exit 0 unless the arguments are wrong).

    VT_API_KEY=... scripts/virustotal.py PLAYER_ROOT MANIFEST [PREVIOUS_MANIFEST PREVIOUS_TAG]

PLAYER_ROOT is the extracted archive's oni-moono folder and MANIFEST its SHA256SUMS. The files
scanned are every .exe and .dll except the ones the deps.json files give to the .NET runtime pack
or to Microsoft's packages (Microsoft.*, System.*): Moono's assemblies and native host, the
facades, the BRUTAL packages and the other third-party libraries. A file whose digest is the one
at the same path in PREVIOUS_MANIFEST (the last release's) is listed as unchanged without asking
VirusTotal. The rest are looked up by SHA-256; unknown ones are uploaded and their analyses polled.
Requests are 15 s apart (the free API's 4 a minute), all within 15 minutes.
"""

import glob
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid

API = "https://www.virustotal.com/api/v3"
GAP = 15.5
DEADLINE = time.monotonic() + 15 * 60
last = 0.0


def request(method, url, body=None, headers=None):
    """One API call, paced; returns (status, parsed JSON or None)."""
    global last
    wait = last + GAP - time.monotonic()
    if wait > 0:
        time.sleep(wait)
    last = time.monotonic()
    req = urllib.request.Request(url, data=body, method=method, headers={"x-apikey": os.environ["VT_API_KEY"], **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            return r.status, json.load(r)
    except urllib.error.HTTPError as e:
        return e.code, None
    except (urllib.error.URLError, OSError, ValueError):
        return 0, None


def time_left():
    return DEADLINE - time.monotonic() > GAP


def upload(path):
    """Uploads a file; returns its analysis id or None."""
    url = API + "/files"
    if os.path.getsize(path) > 32_000_000:
        status, data = request("GET", API + "/files/upload_url")
        if status != 200:
            return None
        url = data["data"]
    boundary = uuid.uuid4().hex
    with open(path, "rb") as f:
        content = f.read()
    body = (
        (
            f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{os.path.basename(path)}"\r\n'
            f"Content-Type: application/octet-stream\r\n\r\n"
        ).encode()
        + content
        + f"\r\n--{boundary}--\r\n".encode()
    )
    status, data = request("POST", url, body, {"Content-Type": f"multipart/form-data; boundary={boundary}"})
    return data["data"]["id"] if status == 200 else None


def hits(results):
    return ", ".join(
        f"{engine}: {r.get('result') or r['category']}" for engine, r in sorted(results.items()) if r.get("category") in ("malicious", "suspicious")
    )


def ours(root):
    """The .exe and .dll files under player/, less the runtime pack's and Microsoft's packages'."""
    player = os.path.join(root, "player")
    theirs = set()
    for deps in glob.glob(os.path.join(player, "*.deps.json")):
        with open(deps, encoding="utf-8") as f:
            d = json.load(f)
        for target in d["targets"].values():
            for name, entry in target.items():
                lib = d["libraries"].get(name, {})
                if lib.get("type") == "runtimepack" or name.startswith(("Microsoft.", "System.")):
                    for section in ("runtime", "native", "resources"):
                        theirs.update(os.path.basename(p) for p in entry.get(section, {}))
    files = []
    for dirpath, _, names in os.walk(player):
        for n in names:
            rel = os.path.relpath(os.path.join(dirpath, n), root).replace(os.sep, "/")
            if n.lower().endswith((".exe", ".dll")) and not (dirpath == player and n in theirs):
                files.append(rel)
    return sorted(files, key=lambda p: (not os.path.basename(p).startswith(("OniMoono", "Restarter")), p))


def read_manifest(path):
    with open(path, encoding="utf-8") as f:
        return {line[66:]: line[:64] for line in f.read().splitlines() if line}


def main():
    if len(sys.argv) not in (3, 5):
        print(__doc__)
        return 2
    root, manifest = sys.argv[1], read_manifest(sys.argv[2])
    previous, previous_tag = (read_manifest(sys.argv[3]), sys.argv[4]) if len(sys.argv) == 5 else ({}, "")
    rows, pending, warnings = {}, {}, []
    files = ours(root)
    for rel in files:
        sha = manifest[rel]
        if previous.get(rel) == sha:
            rows[rel] = (sha, "", "", f"unchanged since {previous_tag}")
            continue
        if not time_left():
            rows[rel] = (sha, "", "", "not checked (out of time)")
            continue
        status, data = request("GET", f"{API}/files/{sha}")
        if status == 200:
            a = data["data"]["attributes"]
            s = a.get("last_analysis_stats", {})
            if not a.get("last_analysis_results"):
                rows[rel] = (sha, "", "", "known, not analysed yet")
                continue
            rows[rel] = (sha, s.get("malicious", 0), s.get("suspicious", 0), hits(a.get("last_analysis_results", {})) or "clean")
        elif status == 404:
            analysis = upload(os.path.join(root, rel)) if time_left() else None
            if analysis:
                pending[rel] = analysis
                rows[rel] = (sha, "", "", "uploaded, analysis not finished in time")
            else:
                rows[rel] = (sha, "", "", "unknown, upload failed")
        else:
            rows[rel] = (sha, "", "", f"lookup failed (HTTP {status})")
    while pending and time_left():
        for rel, analysis in list(pending.items()):
            if not time_left():
                break
            status, data = request("GET", f"{API}/analyses/{analysis}")
            if status == 200 and data["data"]["attributes"].get("status") == "completed":
                a = data["data"]["attributes"]
                s = a.get("stats", {})
                rows[rel] = (rows[rel][0], s.get("malicious", 0), s.get("suspicious", 0), "uploaded; " + (hits(a.get("results", {})) or "clean"))
                del pending[rel]
    out = ["## VirusTotal", "", "| File | SHA-256 | Malicious | Suspicious | Result |", "|---|---|---|---|---|"]
    for rel in files:
        sha, mal, sus, result = rows[rel]
        out.append(f"| `{rel}` | [{sha[:16]}…](https://www.virustotal.com/gui/file/{sha}) | {mal} | {sus} | {result} |")
        if mal or sus:
            warnings.append(f"::warning::VirusTotal: {rel} has {mal} malicious and {sus} suspicious verdicts ({result})")
    text = "\n".join(out) + "\n"
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(text)
    print(text)
    for w in warnings:
        print(w)
    return 0


if __name__ == "__main__":
    sys.exit(main())
