# syntax=docker/dockerfile:1
# Builds and runs src/MoodyBlues.Backend only (tests aren't part of the shipped image).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy just the csproj first so `dotnet restore` is cached across builds unless
# dependencies actually change.
COPY src/MoodyBlues.Backend/MoodyBlues.Backend.csproj src/MoodyBlues.Backend/
RUN dotnet restore src/MoodyBlues.Backend/MoodyBlues.Backend.csproj

COPY src/MoodyBlues.Backend/ src/MoodyBlues.Backend/
RUN dotnet publish src/MoodyBlues.Backend/MoodyBlues.Backend.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Scene post-processing (see Scenes/Processing/SceneProcessingWorker.cs) shells out to
# `gltf-transform optimize`, which itself shells out to KTX-Software's `toktx` for KTX2 texture
# compression -- neither ships with the .NET runtime image, so both are installed here.
#
# Node.js comes from the official prebuilt tarball rather than apt/NodeSource: the NodeSource .deb
# drags in a whole GPG/apt-repo signing chain (gnupg, dirmngr) plus, non-obviously, a full python3
# toolchain as a hard dependency -- none of which `node`/`npm` themselves need. A raw tarball
# extract skips all of that, cutting both download size and install time.
#
# `--mount=type=cache` (needs the `syntax=docker/dockerfile:1` line up top) persists apt's and
# npm's package caches in BuildKit's build cache, separately from this layer's own instruction-text
# cache -- so even if this RUN's text changes later (e.g. a version bump), re-running it doesn't
# mean re-downloading everything from the network again, just re-installing from the local cache.
# See https://docs.docker.com/build/cache/optimize/#use-cache-mounts.
#
# Node.js and KTX-Software both ship separate amd64/arm64 assets -- `dpkg --print-architecture`
# picks the right one so this works on both x86_64 hosts and arm64 hosts (e.g. Apple Silicon, AWS
# Graviton) alike.
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt/lists,sharing=locked \
    --mount=type=cache,target=/root/.npm,sharing=locked \
    apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates xz-utils \
    && NODE_ARCH="$(case "$(dpkg --print-architecture)" in amd64) echo x64 ;; arm64) echo arm64 ;; *) echo unsupported ;; esac)" \
    && if [ "$NODE_ARCH" = "unsupported" ]; then echo "Unsupported architecture: $(dpkg --print-architecture)" >&2; exit 1; fi \
    && curl -fsSL -o /tmp/node.tar.xz \
       "https://nodejs.org/dist/v20.20.2/node-v20.20.2-linux-${NODE_ARCH}.tar.xz" \
    && tar -xJf /tmp/node.tar.xz -C /usr/local --strip-components=1 \
    && rm -f /tmp/node.tar.xz \
    && npm install --global @gltf-transform/cli \
    && KTX_ARCH="$(case "$(dpkg --print-architecture)" in amd64) echo x86_64 ;; arm64) echo arm64 ;; *) echo unsupported ;; esac)" \
    && if [ "$KTX_ARCH" = "unsupported" ]; then echo "Unsupported architecture: $(dpkg --print-architecture)" >&2; exit 1; fi \
    && curl -fsSL -o /tmp/ktx-software.deb \
       "https://github.com/KhronosGroup/KTX-Software/releases/download/v4.4.2/KTX-Software-4.4.2-Linux-${KTX_ARCH}.deb" \
    && apt-get install -y --no-install-recommends /tmp/ktx-software.deb \
    && rm -f /tmp/ktx-software.deb \
    && apt-get purge -y xz-utils \
    && apt-get autoremove -y

# logs/ and scenes/ are written relative to the working directory at runtime
# (see ServerConfig.cs) -- docker-compose.yml mounts volumes over them.
EXPOSE 8765
ENTRYPOINT ["dotnet", "MoodyBlues.Backend.dll"]
