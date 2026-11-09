# Build stage: the full SDK, thrown away after publish.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, on its own layer, so a source-only change does not re-download packages.
COPY src/Quiz/Quiz.csproj src/Quiz/
RUN dotnet restore src/Quiz/Quiz.csproj

COPY src/Quiz/ src/Quiz/
# questions/*.json are copied to the output by the csproj, so they land in /app/publish/questions.
RUN dotnet publish src/Quiz/Quiz.csproj -c Release --no-restore -o /app/publish

# Run stage: ASP.NET runtime only, on Alpine.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .

# Plain HTTP on 8080: Caddy in front terminates TLS, and nothing is published to the host by the image.
ENV ASPNETCORE_HTTP_PORTS=8080
# Workstation GC: the VPS is small and shares its memory with other services.
ENV DOTNET_gcServer=0
EXPOSE 8080

# Non-root. The base image ships an unprivileged "app" user and exports its uid as APP_UID.
# The app keeps all state in memory and writes nothing to disk, so it also runs with a
# read-only root filesystem (the runtime may use a tmpfs at /tmp).
USER $APP_UID

ENTRYPOINT ["dotnet", "Quiz.dll"]
