#!/usr/bin/env bash
# Fails if tracked files contain things CLAUDE.md says must never be committed: Spartan tokens,
# XUIDs, Theater film data, or absolute machine paths. It can't recognise gamertags, so those still
# need a human eye. Run it locally from the repo root: .github/scripts/privacy-check.sh
set -uo pipefail

# With --log <folder>, scans the log files in that folder instead of the repository: the same
# patterns, over what the app wrote while the browser tests ran. Run them with ANCILLA_E2E_LOGS set.
logdir=""
if [ "${1:-}" = "--log" ]; then
  logdir="${2:?--log needs a folder}"
  [ -d "$logdir" ] || { echo "::error::No log folder at $logdir"; exit 1; }
fi

self=".github/scripts/privacy-check.sh"
failed=0

report() {
  echo "::error::$1"
  echo "$2" | sed 's/^/  /'
  failed=1
}

# scan <extended regex>: matching lines in tracked files, or in the log folder with --log.
scan() {
  if [ -n "$logdir" ]; then grep -rnIE "$1" "$logdir" || true
  else git grep -nIE "$1" -- ":!$self" || true
  fi
}

if [ -n "$logdir" ]; then
  [ -n "$(ls -A "$logdir")" ] || { echo "::error::$logdir has no log files"; exit 1; }
else
  # Film data and cache files. Films hold every player's gamertag and XUID.
  files=$(git ls-files | grep -E '\.zlib$|(^|/)chunk[0-9]{3}-type[0-9]\.(bin|zlib)$|(^|/)(scoreboard|manifest|medals|snapshots)\.json$' || true)
  [ -n "$files" ] && report "Theater film or cache data is tracked" "$files"
fi

# Spartan tokens: a JWE header ("eyJhbGciOi") or a v4= / v4%3D prefix followed by one.
hits=$(scan 'eyJhbGciOi|v4(=|%3D)ey[A-Za-z0-9_-]{8}')
[ -n "$hits" ] && report "Something that looks like a Spartan token" "$hits"

# XUIDs: real Xbox accounts are 0x0009_xxxx_xxxx_xxxx, 2533274790395904 to 2814749767106559 in decimal.
# Tests use made-up XUIDs written in hex, so they don't trip this.
hits=$(scan '(^|[^0-9])2(5[3-9]|6[0-9]|7[0-9]|8[01])[0-9]{13}([^0-9]|$)')
[ -n "$hits" ] && report "Something that looks like a XUID" "$hits"

# Absolute paths that would name a machine's account.
hits=$(scan '/Users/[A-Za-z]|/home/[a-z][a-z0-9_-]*/|[A-Za-z]:\\Users\\')
[ -n "$hits" ] && report "An absolute path that names an account" "$hits"

if [ "$failed" -eq 0 ]; then echo "Privacy check passed."; fi
exit "$failed"
