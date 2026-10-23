# Build stage ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so restore is cached independently of source edits.
COPY Bookline.slnx ./
COPY src/Bookline.Api/Bookline.Api.csproj src/Bookline.Api/
COPY tests/Bookline.Tests/Bookline.Tests.csproj tests/Bookline.Tests/
RUN dotnet restore src/Bookline.Api/Bookline.Api.csproj

COPY . .
RUN dotnet publish src/Bookline.Api/Bookline.Api.csproj -c Release -o /app --no-restore

# Runtime stage -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is here only so the container can report its own health.
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build --chown=app:app /app ./

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

# The aspnet image ships a non-root "app" user. Running as root would turn a
# remote code execution bug into container root for free.
USER app

HEALTHCHECK --interval=30s --timeout=3s --start-period=25s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Bookline.Api.dll"]
