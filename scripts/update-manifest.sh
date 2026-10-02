#!/usr/bin/env bash
# Adds the zip in artifacts/ to manifest.json on the `manifest` branch (creating the branch on first use).
# Jellyfin reads it from https://raw.githubusercontent.com/<owner>/<repo>/manifest/manifest.json
#
#   scripts/update-manifest.sh <tag> <owner/repo>
#
# Needs jprm (pip install jprm) and, for the real remote, GH_TOKEN with contents:write.
# REMOTE_URL overrides the git remote (used to test this script against a local repository).
set -euo pipefail

tag="${1:?usage: $0 <tag> <owner/repo>}"
repo="${2:?usage: $0 <tag> <owner/repo>}"
remote="${REMOTE_URL:-https://x-access-token:${GH_TOKEN:?GH_TOKEN is not set}@github.com/${repo}.git}"

zip="$(ls artifacts/*.zip | head -n 1)"
url="https://github.com/${repo}/releases/download/${tag}/$(basename "$zip")"
absolute_zip="$(cd "$(dirname "$zip")" && pwd)/$(basename "$zip")"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if git ls-remote --exit-code --heads "$remote" manifest >/dev/null 2>&1; then
  git clone --quiet --depth 1 --branch manifest "$remote" "$work"
else
  git init --quiet -b manifest "$work"
  git -C "$work" remote add origin "$remote"
fi

[ -f "$work/manifest.json" ] || jprm repo init "$work"
jprm repo add --plugin-url="$url" "$work" "$absolute_zip"

git -C "$work" add manifest.json
if git -C "$work" diff --cached --quiet; then
  echo "manifest.json already lists ${tag}."
  exit 0
fi
git -C "$work" \
  -c user.name="github-actions[bot]" \
  -c user.email="41898282+github-actions[bot]@users.noreply.github.com" \
  commit --quiet -m "Add ${tag}"
git -C "$work" push --quiet origin manifest
echo "manifest.json now lists ${tag}: ${url}"
