# root/selfhost/Dockerfile
# Build using the official, released .NET 11 SDK; runtime runs unprivileged.
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY selfhost/src/NetForge.Api/NetForge.Api.csproj selfhost/src/NetForge.Api/
RUN dotnet restore selfhost/src/NetForge.Api/NetForge.Api.csproj
COPY selfhost/src/NetForge.Api/ selfhost/src/NetForge.Api/
COPY selfhost/db/ selfhost/db/
COPY selfhost/web/ selfhost/web/
WORKDIR /src/selfhost/src/NetForge.Api
# Keep the relative layout of the project; linked db and web files are in /src/selfhost/.
# These copies are intentionally provided at the same relative paths as csproj.
RUN dotnet publish NetForge.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
COPY --from=build /app/publish .
RUN mkdir -p /app/keys && chown -R $APP_UID:$APP_UID /app/keys
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet","NetForge.Api.dll"]
