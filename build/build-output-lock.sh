#!/usr/bin/env bash
# Serialises coverage.sh against mutation-test.sh. Sourced by both, from the repo root.
#
# The two drive the same build output (tests/Alberto.Tests/bin/Release). Coverlet instruments
# those assemblies in place and restores the originals when the run ends, so a Stryker build
# holding the same files makes the restore fail — you get a stack of "cannot access the file
# ... because it is being used by another process", instrumented DLLs left behind in bin, and
# a coverage number collected against a half-restored build. Neither tool notices; both
# report a figure. So they take turns.
#
# mkdir is the lock because it is atomic on every filesystem and needs no flock, which macOS
# does not ship.

_lock_dir="$PWD/artifacts/.build-output.lock"
mkdir -p artifacts
if ! mkdir "$_lock_dir" 2>/dev/null; then
  echo "error: a coverage or mutation run already holds $_lock_dir" >&2
  echo "       Wait for it to finish, or rmdir that directory if it is stale." >&2
  exit 1
fi
trap 'rmdir "$_lock_dir" 2>/dev/null || true' EXIT
