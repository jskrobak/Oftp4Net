# The application is framework-dependent (no native code), so it is compiled once on the build machine's
# platform and the result is copied into the runtime image of each target platform (amd64, arm64).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["global.json", "nuget.config", "Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["Oftp4Net.Server/Oftp4Net.Server.csproj", "Oftp4Net.Server/"]
COPY ["Oftp4Net.Core/Oftp4Net.Core.csproj", "Oftp4Net.Core/"]
COPY ["Oftp4Net.DataLayer/Oftp4Net.DataLayer.csproj", "Oftp4Net.DataLayer/"]
COPY ["Oftp4Net.DependencyInjection/Oftp4Net.DependencyInjection.csproj", "Oftp4Net.DependencyInjection/"]
COPY ["Oftp4Net.Domain/Oftp4Net.Domain.csproj", "Oftp4Net.Domain/"]
COPY ["Oftp4Net.Entity/Oftp4Net.Entity.csproj", "Oftp4Net.Entity/"]
COPY ["Oftp4Net.Services/Oftp4Net.Services.csproj", "Oftp4Net.Services/"]
RUN dotnet restore "Oftp4Net.Server/Oftp4Net.Server.csproj"
COPY . .
RUN dotnet publish "Oftp4Net.Server/Oftp4Net.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish \
        /p:UseAppHost=false --no-restore \
    && mkdir -p /app/data

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# Web UI
EXPOSE 8080
# OFTP over TLS / plain TCP (configure listeners with these ports)
EXPOSE 6619
EXPOSE 3305
COPY --from=build /app/publish .
# Persistent data (received files, outbox, data protection keys, logs), writable by the non-root app user.
COPY --from=build --chown=$APP_UID:$APP_UID /app/data /data
ENV DataDirectory=/data \
    LogDirectory=/data/logs \
    DataProtection__KeysDirectory=/data/keys
VOLUME ["/data"]
USER $APP_UID
# Liveness only (the process answers), so that a database outage does not restart the container. The image has
# no curl, bash talks HTTP itself.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/live HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "Oftp4Net.Server.dll"]
