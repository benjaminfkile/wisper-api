#!/usr/bin/env bash
# Fails when any tracked text file contains a Unicode em dash (U+2014) or en dash (U+2013).
#
# Byte-level: the two UTF-8 sequences (E2 80 94 and E2 80 93) are matched as literal
# bytes with plain grep (no -P), so the result does not depend on the locale. In a
# UTF-8 locale grep matches the characters; in the C locale it matches the raw
# bytes; both find the same lines. Works in Git Bash on Windows, macOS, and Linux.
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
#   2  the scan itself failed (grep error other than "no match")

set -u

cd "$(git rev-parse --show-toplevel)"

EM="$(printf '\xe2\x80\x94')"
EN="$(printf '\xe2\x80\x93')"

# -I skips binary files. git ls-files -z + xargs -0 handles odd filenames.
# grep exits 0 on a hit, 1 on no hit, 2 on error; xargs turns a grep exit of 1
# into 123 and other errors into 124/125/126/127/1.
hits="$(git ls-files -z | xargs -0 grep -I -n -H -e "$EM" -e "$EN" -- 2>&1)"
rc=$?

if [ "$rc" -eq 0 ]; then
  echo "Em dash (U+2014) or en dash (U+2013) found in tracked files:" >&2
  echo "$hits" >&2
  echo "" >&2
  echo "Replace with ASCII punctuation (a hyphen '-' or a double hyphen '--')." >&2
  exit 1
fi

if [ "$rc" -eq 123 ] || [ "$rc" -eq 1 ]; then
  exit 0
fi

echo "check-dashes: scan failed (exit $rc):" >&2
echo "$hits" >&2
exit 2
