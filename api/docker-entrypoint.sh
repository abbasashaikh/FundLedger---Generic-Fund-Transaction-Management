#!/bin/sh
# Selects the process to run from the single FundLedger image.
set -eu
cmd="${1:-api}"
# `shift` with no arguments is a fatal error in POSIX sh, so guard it.
if [ "$#" -gt 0 ]; then shift; fi
case "$cmd" in
  api)     exec dotnet /app/api/FundLedger.Api.dll "$@" ;;
  worker)  exec dotnet /app/worker/FundLedger.Worker.dll "$@" ;;
  migrate) exec dotnet /app/api/FundLedger.Api.dll migrate "$@" ;;
  *)       echo "usage: [api|worker|migrate [--list]]" >&2; exit 64 ;;
esac
