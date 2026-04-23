# MediathekNext Worker — single image, role controlled by MEDIATHEK_ROLE env var
# Roles: standalone (default) | core | worker
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/MediathekNext.Domain/              src/MediathekNext.Domain/
COPY src/MediathekNext.Application/         src/MediathekNext.Application/
COPY src/MediathekNext.Infrastructure/      src/MediathekNext.Infrastructure/
COPY src/MediathekNext.Api/                 src/MediathekNext.Api/
COPY src/MediathekNext.ServiceDefaults/     src/MediathekNext.ServiceDefaults/
COPY src/MediathekNext.Worker/              src/MediathekNext.Worker/

RUN dotnet publish src/MediathekNext.Worker/MediathekNext.Worker.csproj \
    -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ffmpeg required by worker and standalone roles for download execution
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Default role — override with MEDIATHEK_ROLE env var or --role CLI arg
ENV MEDIATHEK_ROLE=standalone
ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080
ENTRYPOINT ["dotnet", "MediathekNext.Worker.dll"]
