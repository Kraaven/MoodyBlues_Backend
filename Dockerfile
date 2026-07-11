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

# logs/ and scenes/ are written relative to the working directory at runtime
# (see ServerConfig.cs) -- docker-compose.yml mounts volumes over them.
EXPOSE 8765
ENTRYPOINT ["dotnet", "MoodyBlues.Backend.dll"]
