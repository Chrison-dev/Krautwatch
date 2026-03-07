FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY ["src/MediathekNext.Worker/MediathekNext.Worker.csproj", "src/MediathekNext.Worker/"]
COPY ["src/MediathekNext.Application/MediathekNext.Application.csproj", "src/MediathekNext.Application/"]
COPY ["src/MediathekNext.Domain/MediathekNext.Domain.csproj", "src/MediathekNext.Domain/"]
COPY ["src/MediathekNext.Infrastructure/MediathekNext.Infrastructure.csproj", "src/MediathekNext.Infrastructure/"]
COPY ["src/MediathekNext.ServiceDefaults/MediathekNext.ServiceDefaults.csproj", "src/MediathekNext.ServiceDefaults/"]

RUN dotnet restore "src/MediathekNext.Worker/MediathekNext.Worker.csproj"

COPY . .
RUN dotnet publish "src/MediathekNext.Worker/MediathekNext.Worker.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime

# Install ffmpeg — pre-installed so no host dependency required
RUN apk add --no-cache ffmpeg

WORKDIR /app
COPY --from=build /app/publish .

VOLUME ["/downloads"]

ENTRYPOINT ["dotnet", "MediathekNext.Worker.dll"]
