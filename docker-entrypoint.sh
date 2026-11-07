#!/bin/sh
# Starts the backend as the non-root `app` user (UID from the .NET base image).
#
# Volumes created by images before this one are owned by root. Hand anything not already owned
# by `app` over to it (only those files, so this is quick after the first start), then drop root
# for good with setpriv, which ships with the Debian base image.
set -eu

if [ "$(id -u)" = "0" ]; then
    find /app/data ! -user app -exec chown app:app {} + 2>/dev/null || true
    exec setpriv --reuid=app --regid=app --init-groups \
        env HOME=/home/app dotnet /app/MoodyBlues.Backend.dll "$@"
fi

exec dotnet /app/MoodyBlues.Backend.dll "$@"
