FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY ["src/MediathekNext.Api/MediathekNext.Api.csproj", "src/MediathekNext.Api/"]
COPY ["src/MediathekNext.Application/MediathekNext.Application.csproj", "src/MediathekNext.Application/"]
COPY ["src/MediathekNext.Domain/MediathekNext.Domain.csproj", "src/MediathekNext.Domain/"]
COPY ["src/MediathekNext.Infrastructure/MediathekNext.Infrastructure.csproj", "src/MediathekNext.Infrastructure/"]
COPY ["src/MediathekNext.ServiceDefaults/MediathekNext.ServiceDefaults.csproj", "src/MediathekNext.ServiceDefaults/"]

RUN dotnet restore "src/MediathekNext.Api/MediathekNext.Api.csproj"

COPY . .
RUN dotnet publish "src/MediathekNext.Api/MediathekNext.Api.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "MediathekNext.Api.dll"]
