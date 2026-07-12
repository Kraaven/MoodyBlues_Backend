# Builds and runs src/MoodyBlues.Backend only (tests aren't part of the shipped image).
#
# moodyblues-backend-tools (referenced below) is the ASP.NET runtime image plus
# Node.js/@gltf-transform/cli/toktx -- built separately from docker/tools.Dockerfile (see that
# file, and deploy.sh's ensure_tools_image) so that slow, rarely-changing layer isn't rebuilt on
# every app-code deploy. Override MOODYBLUES_TOOLS_IMAGE at build time if you've tagged it
# differently. Must be declared before the first FROM to be usable in a later FROM's image ref.
ARG MOODYBLUES_TOOLS_IMAGE=moodyblues-backend-tools:1

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy just the csproj first so `dotnet restore` is cached across builds unless
# dependencies actually change.
COPY src/MoodyBlues.Backend/MoodyBlues.Backend.csproj src/MoodyBlues.Backend/
RUN dotnet restore src/MoodyBlues.Backend/MoodyBlues.Backend.csproj

COPY src/MoodyBlues.Backend/ src/MoodyBlues.Backend/
RUN dotnet publish src/MoodyBlues.Backend/MoodyBlues.Backend.csproj \
    -c Release -o /app --no-restore

FROM ${MOODYBLUES_TOOLS_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /app .

# logs/ and scenes/ are written relative to the working directory at runtime
# (see ServerConfig.cs) -- docker-compose.yml mounts volumes over them.
EXPOSE 8765
ENTRYPOINT ["dotnet", "MoodyBlues.Backend.dll"]
