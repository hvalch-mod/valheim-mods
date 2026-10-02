#!/usr/bin/env python3
"""Upload dist/hvalch-<Name>-<Version>.zip to Thunderstore (team hvalch, community valheim).

Usage: scripts/publish-thunderstore.py <Name> [--check]
  --check  only validate the manifest against the team (tests the token); uploads nothing.

Token: .thunderstore_token (service account of team hvalch). Categories: src/<Name>/thunderstore.json.
Run scripts/package.sh <Name> first. Publishing is public and a version can't be reused.
Hexium is the primary site: the version must already be published there, or this refuses to run.
"""
import base64, json, os, sys, urllib.request, urllib.error, urllib.parse

API = "https://thunderstore.io"
HEXIUM = "https://valheim.hexium.gg"
TEAM = "hvalch"
COMMUNITY = "valheim"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def request(method, url, body=None, data=None, auth=True, token=None):
    headers = {"User-Agent": "hvalch-publish/1.0"}
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    # Upload part URLs are presigned (S3); an extra Authorization header breaks them.
    if auth and urllib.parse.urlparse(url).netloc == urllib.parse.urlparse(API).netloc:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req) as r:
            raw = r.read()
            return r.headers, (json.loads(raw) if raw and r.headers.get_content_type() == "application/json" else None)
    except urllib.error.HTTPError as e:
        sys.exit(f"{method} {url.split('?')[0]} -> {e.code}: {e.read().decode(errors='replace')[:2000]}")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    check = "--check" in sys.argv
    if len(args) != 1:
        sys.exit(__doc__)
    name = args[0]
    with open(os.path.join(ROOT, ".thunderstore_token")) as f:
        token = f.read().strip()
    with open(os.path.join(ROOT, "src", name, "thunderstore.json")) as f:
        categories = json.load(f)["categories"]
    version = next(l.split(">")[1].split("<")[0] for l in open(os.path.join(ROOT, "src", name, f"{name}.csproj")) if "<Version>" in l)
    zip_path = os.path.join(ROOT, "dist", f"{TEAM}-{name}-{version}.zip")
    if not os.path.exists(zip_path):
        sys.exit(f"missing {zip_path}; run scripts/package.sh {name}")

    # Hexium first: never put a version on Thunderstore that isn't on Hexium.
    try:
        request("GET", f"{HEXIUM}/api/experimental/package/{TEAM}/{name}/{version}/", auth=False)
    except SystemExit:
        sys.exit(f"{TEAM}-{name}-{version} is not on Hexium; publish it there first")
    print(f"{TEAM}-{name}-{version} is on Hexium")

    import zipfile
    manifest = base64.b64encode(zipfile.ZipFile(zip_path).read("manifest.json")).decode()
    _, res = request("POST", f"{API}/api/experimental/submission/validate/manifest-v1/",
                     {"namespace": TEAM, "manifest_data": manifest}, token=token)
    print(f"manifest ok for {TEAM}-{name}-{version}: {res}")
    if check:
        return

    size = os.path.getsize(zip_path)
    _, init = request("POST", f"{API}/api/experimental/usermedia/initiate-upload/",
                      {"filename": os.path.basename(zip_path), "file_size_bytes": size}, token=token)
    uuid = init["user_media"]["uuid"]
    parts = []
    with open(zip_path, "rb") as f:
        for p in init["upload_urls"]:
            f.seek(p["offset"])
            headers, _ = request("PUT", p["url"], data=f.read(p["length"]), token=token)
            parts.append({"PartNumber": p["part_number"], "ETag": headers["ETag"]})
    request("POST", f"{API}/api/experimental/usermedia/{uuid}/finish-upload/", {"parts": parts}, token=token)
    _, res = request("POST", f"{API}/api/experimental/submission/submit/", {
        "upload_uuid": uuid,
        "author_name": TEAM,
        "communities": [COMMUNITY],
        "community_categories": {COMMUNITY: categories},
        "categories": [],
        "has_nsfw_content": False,
    }, token=token)
    v = res.get("package_version", {}) if res else {}
    print(f"published {v.get('full_name', name)}: {v.get('download_url', res)}")


if __name__ == "__main__":
    main()
