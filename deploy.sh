#!/usr/bin/env bash
# MoodyBlues Backend -- one-file deploy/manage script for a Linux host.
#
# Drop this file anywhere on the box and run it: on first run it clones the
# repo (if not already present next to this script or in ./MoodyBlues_Backend),
# builds the Docker images, and starts Postgres + the backend. Re-run it with
# the commands below to manage the running stack.
#
# Usage: ./deploy.sh [start|stop|restart|reset|logs|status]
#
#   start    (default) Build (if needed) and start the stack.
#   stop     Stop the stack; containers/volumes are kept, so 'start' is fast.
#   restart  stop, then start.
#   reset    Stop the stack, git fetch/pull the latest changes, rebuild the
#            backend image, and start the stack again -- the "update and
#            restart" command.
#   logs     Tail logs from both containers.
#   status   Show container status.
#
# All state (Postgres data, uploaded scenes, logs) lives in Docker named
# volumes, so 'reset' and 'restart' don't lose any data.
set -euo pipefail

REPO_URL="https://github.com/Kraaven/MoodyBlues_Backend.git"
REPO_DIR="${MOODYBLUES_REPO_DIR:-MoodyBlues_Backend}"

# Node.js/@gltf-transform/cli/toktx pre-installed on top of the ASP.NET runtime image (see
# docker/tools.Dockerfile) -- the backend's Dockerfile FROMs this tag rather than installing those
# tools itself, so that slow apt/npm layer is only rebuilt when this tag changes, not on every
# app-code deploy. Bump this (and docker/tools.Dockerfile) together when the tools need updating.
TOOLS_IMAGE="moodyblues-backend-tools:1"

resolve_repo_root() {
    local script_dir
    script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

    if [ -d "$script_dir/.git" ]; then
        REPO_ROOT="$script_dir"
        return
    fi
    if [ -d "$REPO_DIR/.git" ]; then
        REPO_ROOT="$(pwd)/$REPO_DIR"
        return
    fi

    echo "Cloning $REPO_URL into ./$REPO_DIR ..."
    git clone "$REPO_URL" "$REPO_DIR"
    REPO_ROOT="$(pwd)/$REPO_DIR"
}

compose() {
    (cd "$REPO_ROOT" && docker compose "$@")
}

# Builds $TOOLS_IMAGE from docker/tools.Dockerfile if it isn't already sitting in the local image
# store -- skipped entirely on every deploy after the first, since that tag never changes just
# because app code did. Delete the image (or bump TOOLS_IMAGE above) to force a rebuild, e.g. after
# editing docker/tools.Dockerfile.
ensure_tools_image() {
    if docker image inspect "$TOOLS_IMAGE" >/dev/null 2>&1; then
        return
    fi
    echo "Building tools base image ($TOOLS_IMAGE) -- one-time cost, several minutes..."
    docker build -t "$TOOLS_IMAGE" -f "$REPO_ROOT/docker/tools.Dockerfile" "$REPO_ROOT"
}

cmd_start() {
    ensure_tools_image
    echo "Building and starting the stack..."
    compose up -d --build
    compose ps
}

cmd_stop() {
    echo "Stopping the stack..."
    compose stop
}

cmd_reset() {
    echo "Stopping the stack..."
    compose down
    echo "Pulling latest changes..."
    (cd "$REPO_ROOT" && git fetch origin && git pull --ff-only)
    ensure_tools_image
    echo "Rebuilding and starting the stack..."
    compose up -d --build
    compose ps
}

cmd_logs() {
    compose logs -f
}

cmd_status() {
    compose ps
}

resolve_repo_root

case "${1:-start}" in
    start)   cmd_start ;;
    stop)    cmd_stop ;;
    restart) cmd_stop; cmd_start ;;
    reset)   cmd_reset ;;
    logs)    cmd_logs ;;
    status)  cmd_status ;;
    *)
        echo "Usage: $0 {start|stop|restart|reset|logs|status}" >&2
        exit 1
        ;;
esac
