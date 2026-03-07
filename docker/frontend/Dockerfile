FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY ["src/MediathekNext.Web/MediathekNext.Web.csproj", "src/MediathekNext.Web/"]
COPY ["src/MediathekNext.ServiceDefaults/MediathekNext.ServiceDefaults.csproj", "src/MediathekNext.ServiceDefaults/"]

RUN dotnet restore "src/MediathekNext.Web/MediathekNext.Web.csproj"

COPY . .
RUN dotnet publish "src/MediathekNext.Web/MediathekNext.Web.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "MediathekNext.Web.dll"]
