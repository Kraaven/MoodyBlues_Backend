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

# logs/ and scenes/ are written relative to the working directory at runtime
# (see ServerConfig.cs) -- docker-compose.yml mounts volumes over them.
EXPOSE 8765
ENTRYPOINT ["dotnet", "MoodyBlues.Backend.dll"]
