#!/bin/sh
set -eu

mode="${1:-api}"

case "$mode" in
  api)
    cd /app/api
    exec dotnet Phub.Api.dll
    ;;
  worker)
    cd /app/worker
    exec dotnet Phub.Worker.dll
    ;;
  *)
    exec "$@"
    ;;
esac
