# syntax=docker/dockerfile:1

# Build on the build machine's own architecture and let the .NET SDK cross-compile for the target (amd64, arm64), so
# nothing runs under emulation: an arm64 Mac builds amd64 images, and CI builds both.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/Library.Web/Library.Web.csproj src/Library.Web/
RUN dotnet restore src/Library.Web/Library.Web.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/Library.Web/Library.Web.csproj -c Release -a $TARGETARCH --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="3MF Library" \
      org.opencontainers.image.description="Self-hosted preview site for a folder of 3D print files (3MF, STL)"
WORKDIR /app
COPY --from=build /app .

ENV LIBRARY_PATH=/library \
    DATA_PATH=/data \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Overridden by `user:` in docker-compose.yml (PUID/PGID) so /data is owned by a known UID on the host.
USER $APP_UID
ENTRYPOINT ["dotnet", "Library.Web.dll"]
