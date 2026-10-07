#!/bin/sh
# Selects the process to run from the single FundLedger image.
set -eu
case "${1:-api}" in
  api)     shift 2>/dev/null || true; exec dotnet /app/api/FundLedger.Api.dll "$@" ;;
  worker)  shift; exec dotnet /app/worker/FundLedger.Worker.dll "$@" ;;
  migrate) shift; exec dotnet /app/api/FundLedger.Api.dll migrate "$@" ;;
  *)       echo "usage: [api|worker|migrate [--list]]" >&2; exit 64 ;;
esac
