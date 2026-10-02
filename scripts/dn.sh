#!/usr/bin/env bash
# Runs `dotnet <args>` in the .NET 10 SDK container, in the repo root, with a persistent NuGet cache.
# Needs only Docker. Example: scripts/dn.sh test -c Release
set -uo pipefail
cd "$(dirname "$0")/.."
uid="$(id -u)"; gid="$(id -g)"
docker run --rm --cpus 2 --memory 3g \
  -v "$PWD":/src -w /src \
  -v jf-nuget:/root/.nuget \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c 'dotnet "$@"; rc=$?; chown -R '"$uid:$gid"' /src 2>/dev/null; exit $rc' dotnet "$@"
