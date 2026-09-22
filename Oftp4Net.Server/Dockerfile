FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
# Web UI
EXPOSE 8080
# OFTP over TLS / plain TCP (configure listeners with these ports)
EXPOSE 6619
EXPOSE 3305

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
WORKDIR "/src/Oftp4Net.Server"
RUN dotnet build "Oftp4Net.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Oftp4Net.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Persistent data: received files, data protection keys, logs. Mount a volume here.
ENV LogDirectory=/data/logs \
    DataProtection__KeysDirectory=/data/keys \
    UploadBasePath=/data/uploads
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Oftp4Net.Server.dll"]
