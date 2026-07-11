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

cmd_start() {
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
