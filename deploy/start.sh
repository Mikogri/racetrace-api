#!/bin/sh
set -eu
# Railway mounts volumes as root. Change ownership only of our fixed data directory.
mkdir -p /data/fantasy
chown -R pwuser:pwuser /data/fantasy
export ASPNETCORE_URLS="http://0.0.0.0:${PORT:-8080}"
exec runuser -u pwuser -- dotnet /app/racetrace-api.dll
