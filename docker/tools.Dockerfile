# Base image for the backend's runtime stage (see ../Dockerfile) -- adds Node.js, the
# @gltf-transform/cli npm package, and KTX-Software's `toktx` on top of the plain ASP.NET runtime
# image, for the Draco/KTX2 scene optimization pass (see
# src/MoodyBlues.Backend/Scenes/Processing/SceneProcessingWorker.cs).
#
# This lives in its own Dockerfile/image, built and tagged once (see deploy.sh's ensure_tools_image
# and the README's "Scene optimization pipeline" section) rather than as a RUN step in the main
# Dockerfile. It's a slow layer (apt + npm installs, several minutes on a fresh box) that almost
# never needs to change, whereas the main Dockerfile changes on every app-code deploy -- keeping it
# separate means day-to-day deploys never pay that cost, only bumping TOOLS_IMAGE_TAG below does.
#
# To pick up a new KTX-Software/Node version: edit this file, then bump TOOLS_IMAGE_TAG in
# deploy.sh so it's rebuilt (and re-tagged) instead of reusing the image already sitting on disk.
FROM mcr.microsoft.com/dotnet/aspnet:9.0

# KTX-Software ships separate amd64/arm64 .deb assets (unlike NodeSource's setup script, which
# auto-detects the architecture on its own) -- `dpkg --print-architecture` picks the right one so
# this works on both x86_64 hosts and arm64 hosts (e.g. Apple Silicon, AWS Graviton) alike.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates gnupg xz-utils \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install --global @gltf-transform/cli \
    && KTX_ARCH="$(case "$(dpkg --print-architecture)" in amd64) echo x86_64 ;; arm64) echo arm64 ;; *) echo unsupported ;; esac)" \
    && if [ "$KTX_ARCH" = "unsupported" ]; then echo "Unsupported architecture: $(dpkg --print-architecture)" >&2; exit 1; fi \
    && curl -fsSL -o /tmp/ktx-software.deb \
       "https://github.com/KhronosGroup/KTX-Software/releases/download/v4.4.2/KTX-Software-4.4.2-Linux-${KTX_ARCH}.deb" \
    && apt-get install -y --no-install-recommends /tmp/ktx-software.deb \
    && rm -f /tmp/ktx-software.deb \
    && apt-get purge -y gnupg xz-utils \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*
