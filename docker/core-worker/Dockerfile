FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY ["src/MediathekNext.CoreWorker/MediathekNext.CoreWorker.csproj", "src/MediathekNext.CoreWorker/"]
COPY ["src/MediathekNext.Application/MediathekNext.Application.csproj", "src/MediathekNext.Application/"]
COPY ["src/MediathekNext.Domain/MediathekNext.Domain.csproj", "src/MediathekNext.Domain/"]
COPY ["src/MediathekNext.Infrastructure/MediathekNext.Infrastructure.csproj", "src/MediathekNext.Infrastructure/"]
COPY ["src/MediathekNext.ServiceDefaults/MediathekNext.ServiceDefaults.csproj", "src/MediathekNext.ServiceDefaults/"]

RUN dotnet restore "src/MediathekNext.CoreWorker/MediathekNext.CoreWorker.csproj"

COPY . .
RUN dotnet publish "src/MediathekNext.CoreWorker/MediathekNext.CoreWorker.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# SQLite db and downloads live on shared volumes (see DR-002)
VOLUME ["/data", "/downloads"]

ENTRYPOINT ["dotnet", "MediathekNext.CoreWorker.dll"]
