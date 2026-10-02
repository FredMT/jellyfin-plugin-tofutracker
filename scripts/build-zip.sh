#!/usr/bin/env bash
# Builds the installable plugin zip into artifacts/ using the .NET 10 SDK container (needs only Docker).
# The zip holds Jellyfin.Plugin.TofuTracker.dll and meta.json, which is what Jellyfin's plugin loader expects.
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
    chown -R '"$(id -u):$(id -g)"' /src
  '
ls -la artifacts
