#!/usr/bin/env bash
# Compiles the plugin against the assemblies of a real Jellyfin install instead of the NuGet packages.
# A clean build proves every server API the plugin uses exists with the same signature in that install.
#
#   scripts/verify-against-server.sh /path/to/dir/with/MediaBrowser.Controller.dll
#
# On macOS the folder is /Applications/Jellyfin.app/Contents/MacOS. Copy it to the machine that has Docker.
set -euo pipefail
server_dir="${1:?usage: $0 <jellyfin install dir>}"
cd "$(dirname "$0")/.."
docker run --rm --cpus 2 --memory 3g \
  -v "$PWD":/src -w /src \
  -v "$(cd "$server_dir" && pwd)":/server:ro \
  -v jf-nuget:/root/.nuget \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c 'dotnet build src/Jellyfin.Plugin.TofuTracker -c Release -p:ServerAssembliesDir=/server -o /tmp/verify-out; rc=$?; chown -R '"$(id -u):$(id -g)"' /src 2>/dev/null; exit $rc'
