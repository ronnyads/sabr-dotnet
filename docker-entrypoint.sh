#!/bin/sh
set -eu

mode="${1:-api}"

case "$mode" in
  api)
    exec dotnet /app/api/Phub.Api.dll
    ;;
  worker)
    exec dotnet /app/worker/Phub.Worker.dll
    ;;
  *)
    exec "$@"
    ;;
esac
