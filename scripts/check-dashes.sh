#!/usr/bin/env bash
# Fails when any tracked file contains a Unicode em dash (U+2014) or en dash (U+2013).
# Byte-level so it does not depend on the shell's locale or the file's encoding
# declaration; portable across Git Bash on Windows, macOS, and Linux CI runners.
#
# Rationale (task #206): every ASCII keyboard has a hyphen; every non-ASCII dash
# in source, tests, or docs is a copy-paste artefact from a rich-text editor or
# an LLM. Left in place they multiply, produce diff churn on every re-render,
# break grep for '--' style flags, and read as bugs in a few narrow spots (an
# XML comment inside a .csproj rejects '--', for example). One check keeps the
# tree ASCII-clean.
#
# Exit codes:
#   0  no dashes found
#   1  at least one dash found; hits are printed with file:line:content

set -eu

cd "$(git rev-parse --show-toplevel)"

# U+2014 = 0xE2 0x80 0x94, U+2013 = 0xE2 0x80 0x93. -P (perl regex) is available on
# GNU grep and macOS grep from the git for windows bundle; fall back to a byte pair
# match with -E if -P is missing.
if grep --help 2>&1 | grep -q -- '-P'; then
  PATTERN_FLAG=P
  PATTERN='\xe2\x80[\x93\x94]'
else
  PATTERN_FLAG=E
  # POSIX ERE cannot express arbitrary bytes; the fallback matches the two
  # sequences literally via printf.
  PATTERN="$(printf '\xe2\x80\x94|\xe2\x80\x93')"
fi

# git ls-files with -z + xargs -0 handles filenames with spaces / newlines.
hits="$(git ls-files -z | xargs -0 grep -"$PATTERN_FLAG" -n -H -- "$PATTERN" 2>/dev/null || true)"

if [ -n "$hits" ]; then
  echo "Em dash (U+2014) or en dash (U+2013) found in tracked files:" >&2
  echo "$hits" >&2
  echo "" >&2
  echo "Replace with ASCII punctuation (a hyphen '-' or a double hyphen '--')." >&2
  exit 1
fi

exit 0
