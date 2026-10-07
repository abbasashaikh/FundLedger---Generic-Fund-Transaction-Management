#!/bin/sh
# Selects the process to run from the single FundLedger image.
set -eu
cmd="${1:-api}"
# `shift` with no arguments is a fatal error in POSIX sh, so guard it.
if [ "$#" -gt 0 ]; then shift; fi
case "$cmd" in
  api)     exec dotnet /app/api/FundLedger.Api.dll "$@" ;;
  worker)  exec dotnet /app/worker/FundLedger.Worker.dll "$@" ;;
  migrate|bootstrap|generate-jwt-key) exec dotnet /app/api/FundLedger.Api.dll "$cmd" "$@" ;;
  *)       echo "usage: [api|worker|migrate [--list]|bootstrap ...|generate-jwt-key]" >&2; exit 64 ;;
esac
