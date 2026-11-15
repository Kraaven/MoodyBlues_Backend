#!/bin/sh
# Starts the backend as the non-root `app` user (UID from the .NET base image). The image creates
# /app/data owned by `app`, so fresh volumes mounted there are writable without any ownership fix.
# If the container is started as root, drop to `app` with setpriv (part of the Debian base image).
set -eu

if [ "$(id -u)" = "0" ]; then
    exec setpriv --reuid=app --regid=app --init-groups \
        env HOME=/home/app dotnet /app/MoodyBlues.Backend.dll "$@"
fi

exec dotnet /app/MoodyBlues.Backend.dll "$@"
