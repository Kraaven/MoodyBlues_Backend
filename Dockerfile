# syntax=docker/dockerfile:1
# Builds and runs src/MoodyBlues.Backend only (tests are not part of the shipped image).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just the csproj first so `dotnet restore` is cached unless dependencies change.
COPY src/MoodyBlues.Backend/MoodyBlues.Backend.csproj src/MoodyBlues.Backend/
RUN dotnet restore src/MoodyBlues.Backend/MoodyBlues.Backend.csproj

COPY src/MoodyBlues.Backend/ src/MoodyBlues.Backend/
RUN dotnet publish src/MoodyBlues.Backend/MoodyBlues.Backend.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Scene optimization (Scenes/Processing/GltfTransformOptimizer.cs) shells out to
# `gltf-transform optimize`, which uses KTX-Software's `toktx` for KTX2 textures. Neither ships
# with the .NET image, so both are installed here.
#
# GLTF_TRANSFORM_VERSION is pinned on purpose: the optimizer passes explicit flags, and an
# unpinned install once silently broke every upload when a flag was removed upstream. Bump it
# deliberately and re-check GltfTransformOptimizer.BuildArguments against `optimize --help`.
#
# Node.js comes from the official tarball (no apt repo / GPG chain). Assets are chosen per
# architecture so the image builds on amd64 and arm64 hosts.
ARG NODE_VERSION=24.22.2
ARG GLTF_TRANSFORM_VERSION=4.5.1
ARG KTX_VERSION=4.4.2
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt/lists,sharing=locked \
    --mount=type=cache,target=/root/.npm,sharing=locked \
    apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates xz-utils \
    && NODE_ARCH="$(case "$(dpkg --print-architecture)" in amd64) echo x64 ;; arm64) echo arm64 ;; *) echo unsupported ;; esac)" \
    && if [ "$NODE_ARCH" = "unsupported" ]; then echo "Unsupported architecture: $(dpkg --print-architecture)" >&2; exit 1; fi \
    && curl -fsSL -o /tmp/node.tar.xz "https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-${NODE_ARCH}.tar.xz" \
    && tar -xJf /tmp/node.tar.xz -C /usr/local --strip-components=1 \
    && rm -f /tmp/node.tar.xz \
    && npm install --global "@gltf-transform/cli@${GLTF_TRANSFORM_VERSION}" \
    && KTX_ARCH="$(case "$(dpkg --print-architecture)" in amd64) echo x86_64 ;; arm64) echo arm64 ;; *) echo unsupported ;; esac)" \
    && curl -fsSL -o /tmp/ktx-software.deb \
       "https://github.com/KhronosGroup/KTX-Software/releases/download/v${KTX_VERSION}/KTX-Software-${KTX_VERSION}-Linux-${KTX_ARCH}.deb" \
    && apt-get install -y --no-install-recommends /tmp/ktx-software.deb \
    && rm -f /tmp/ktx-software.deb \
    && apt-get purge -y xz-utils \
    && apt-get autoremove -y

# data/scenes and data/sessions are written relative to the working directory (ServerConfig.cs);
# docker-compose.yml mounts volumes over them. They are created here, owned by the image's
# non-root `app` user, so fresh volumes inherit that ownership.
RUN mkdir -p /app/data/scenes /app/data/sessions && chown -R app:app /app/data
COPY --chmod=755 docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh

EXPOSE 8765
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
    CMD curl -fsS http://localhost:8765/health || exit 1

# The entrypoint starts as root only to hand volumes created by older images (owned by root) to
# the `app` user, then runs the server as `app`. Nothing else runs as root.
ENTRYPOINT ["docker-entrypoint.sh"]
