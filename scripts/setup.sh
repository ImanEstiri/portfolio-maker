#!/usr/bin/env bash
#
# setup.sh — one-command setup for working on cv-maker (not for running it: that is
# `docker compose up --build`, which needs nothing but Docker).
#
# Usage:
#   ./scripts/setup.sh            check prerequisites, install the pre-commit hook
#   ./scripts/setup.sh --check    strict: a missing prerequisite is a failure
#
# There are no secrets to initialise and no mandatory configuration: the stack runs offline
# against a mock agent runtime and a local blob emulator. What is left is the part that
# applies — name the prerequisites, install the hook, and say what will be refused if a step
# was skipped.

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "${REPO_ROOT}"

STRICT=0
[ "${1:-}" = "--check" ] && STRICT=1

red()   { printf '\033[0;31m%s\033[0m\n' "$*"; }
green() { printf '\033[0;32m%s\033[0m\n' "$*"; }
amber() { printf '\033[0;33m%s\033[0m\n' "$*"; }
dim()   { printf '\033[0;90m%s\033[0m\n' "$*"; }
step()  { printf '\n\033[1m%s\033[0m\n' "$*"; }

DEGRADED=0

step "1. Prerequisites"

if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks | grep -q '^9\.'; then
  green "  .NET SDK 9 found."
else
  DEGRADED=1
  amber "  .NET 9 SDK not found — needed to build and test outside Docker."
  echo  "  Install: https://dotnet.microsoft.com/download/dotnet/9.0"
fi

if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  green "  Docker is running."
else
  DEGRADED=1
  amber "  Docker is not running — needed for 'docker compose up --build' and for the"
  echo  "  renderer, which cannot be run without its TeX Live image."
  echo  "  Install: https://docs.docker.com/get-docker/"
fi

step "2. Secret scanner"

if command -v gitleaks >/dev/null 2>&1; then
  green "  gitleaks on PATH — the hook will use it directly."
elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  green "  gitleaks not on PATH, but Docker is running — the hook falls back to the"
  dim   "  same container image CI uses."
else
  DEGRADED=1
  amber "  No secret scanner available. The hook installed below REFUSES TO COMMIT without"
  echo  "  one — by design, not a bug to work around. Install gitleaks:"
  echo  "    https://github.com/gitleaks/gitleaks#installing   (macOS: brew install gitleaks)"
fi

step "3. Git hook"

HOOK_SRC="${REPO_ROOT}/scripts/hooks/pre-commit"
HOOK_DST="$(git rev-parse --git-path hooks)/pre-commit"
mkdir -p "$(dirname "${HOOK_DST}")"

if [ -e "${HOOK_DST}" ] && ! cmp -s "${HOOK_SRC}" "${HOOK_DST}"; then
  cp "${HOOK_DST}" "${HOOK_DST}.backup"
  dim "  Existing hook backed up to pre-commit.backup"
fi
cp "${HOOK_SRC}" "${HOOK_DST}"
chmod +x "${HOOK_DST}"
green "  pre-commit installed → ${HOOK_DST}"

step "Ready"

echo "  dotnet build CvMaker.sln && dotnet test CvMaker.sln   build and run the unit tests"
echo "  docker compose up --build                             the whole stack"
echo "  ./scripts/scan-secrets.sh                             mirror the CI secret scan"

if [ "${DEGRADED}" -eq 1 ]; then
  echo
  if [ "${STRICT}" -eq 1 ]; then
    red "setup --check: a prerequisite is missing."
    exit 1
  fi
  amber "Setup finished, with the gaps noted above."
fi
