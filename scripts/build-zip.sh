#!/usr/bin/env bash
# Builds the installable plugin zip into artifacts/ using the .NET 10 SDK container (needs only Docker).
# The zip holds Jellyfin.Plugin.TofuTracker.dll, tofutracker.png and meta.json, which is what Jellyfin's plugin loader expects.
set -euo pipefail
cd "$(dirname "$0")/.."
version="$(sed -nE 's/^version: "?([0-9.]+)"?$/\1/p' build.yaml)"
rm -rf artifacts && mkdir -p artifacts
docker run --rm --cpus 2 --memory 3g \
  -v "$PWD":/src -w /src \
  -v jf-nuget:/root/.nuget \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c '
    set -e
    apt-get update -qq >/dev/null && apt-get install -y -qq python3-venv >/dev/null
    python3 -m venv /tmp/venv && /tmp/venv/bin/pip install -q jprm
    /tmp/venv/bin/jprm --verbosity=debug plugin build . --output=artifacts --version='"$version"' \
      --dotnet-framework=net10.0 --dotnet-configuration=Release
    # jprm writes the image name as "image"; the server reads "imagePath" from meta.json of a plugin that sits in its
    # plugins folder, so add it (and refresh the checksum file, which covers the rewritten zip).
    python3 - <<\PY
import glob, hashlib, json, os, zipfile
for path in glob.glob("artifacts/*.zip"):
    tmp = path + ".tmp"
    with zipfile.ZipFile(path) as src, zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as dst:
        for item in src.infolist():
            data = src.read(item.filename)
            if item.filename == "meta.json":
                meta = json.loads(data)
                if "image" in meta:
                    meta["imagePath"] = meta["image"]
                data = json.dumps(meta, sort_keys=True, indent=4).encode()
            dst.writestr(item, data)
    os.replace(tmp, path)
    digest = hashlib.md5(open(path, "rb").read()).hexdigest()
    open(path + ".md5sum", "w").write(digest + " *" + os.path.basename(path) + "\n")
PY
    chown -R '"$(id -u):$(id -g)"' /src
  '
ls -la artifacts
