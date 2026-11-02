#!/usr/bin/env bash
# Calcule la prochaine version SemVer à partir du dernier tag vX.Y.Z.
# Incrément : patch par défaut ; minor/major via libellé de PR (release:minor, release:major)
# ou préfixe de commit (feat:/minor: -> minor ; "type!:" ou major: -> major).
# Usage : next-version.sh [sha] ; entrées facultatives : PR_LABELS (libellés séparés par des virgules).
set -euo pipefail
sha="${1:-HEAD}"
last="$(git tag --list 'v[0-9]*.[0-9]*.[0-9]*' --sort=-v:refname | head -n1)"
last="${last:-v0.0.0}"
IFS=. read -r major minor patch <<<"${last#v}"
subject="$(git log -1 --format=%s "$sha")"
labels="${PR_LABELS:-}"
level=patch
if [[ "$labels" == *release:major* || "$subject" =~ ^[a-z]+(\(.+\))?!: || "$subject" =~ ^major: ]]; then level=major
elif [[ "$labels" == *release:minor* || "$subject" =~ ^(feat|minor)(\(.+\))?: ]]; then level=minor; fi
case "$level" in
  major) major=$((major+1)); minor=0; patch=0 ;;
  minor) minor=$((minor+1)); patch=0 ;;
  *) patch=$((patch+1)) ;;
esac
echo "${major}.${minor}.${patch}"
