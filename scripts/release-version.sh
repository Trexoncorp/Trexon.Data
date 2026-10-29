#!/usr/bin/env bash
set -euo pipefail

# NuGet versions are sourced only from vMAJOR.MINOR.PATCH[-PRERELEASE] tags.
# Build metadata is deliberately excluded to avoid NuGet version collisions.
tag="${1:-}"
number='(0|[1-9][0-9]*)'
pattern="^v${number}\\.${number}\\.${number}(-[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*)?$"

if [[ ! "$tag" =~ $pattern ]]; then
  echo 'Expected a tag such as v0.1.0 or v0.1.0-alpha.1.' >&2
  exit 1
fi

version="${tag#v}"
if [[ "$version" == *-* ]]; then
  prerelease="${version#*-}"
  IFS='.' read -r -a identifiers <<< "$prerelease"
  for identifier in "${identifiers[@]}"; do
    if [[ "$identifier" =~ ^[0-9]+$ && "$identifier" =~ ^0[0-9]+$ ]]; then
      echo 'Numeric prerelease identifiers must not have leading zeros.' >&2
      exit 1
    fi
  done
fi

printf '%s\n' "$version"
