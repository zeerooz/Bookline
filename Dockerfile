# Single-image deployment: the API serves the built frontend from wwwroot.
#
# docker-compose runs the two separately behind nginx, which is the better
# shape for a real deployment. This image exists for platforms that bill per
# service: one process, one URL, same origin, so no CORS and no cross-service
# configuration to keep in sync.

# ---- frontend -------------------------------------------------------------
FROM node:24-alpine AS frontend
WORKDIR /app

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./
# Same origin as the API, so the client calls /api with no base URL.
ENV VITE_API_BASE_URL=""
RUN npm run build

# ---- backend --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src

COPY backend/Bookline.slnx ./
COPY backend/src/Bookline.Api/Bookline.Api.csproj src/Bookline.Api/
COPY backend/tests/Bookline.Tests/Bookline.Tests.csproj tests/Bookline.Tests/
RUN dotnet restore src/Bookline.Api/Bookline.Api.csproj

COPY backend/ ./
RUN dotnet publish src/Bookline.Api/Bookline.Api.csproj -c Release -o /app --no-restore

# ---- runtime --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install --no-install-recommends -y curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=backend --chown=app:app /app ./
COPY --from=frontend --chown=app:app /app/dist ./wwwroot

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

USER app

HEALTHCHECK --interval=30s --timeout=3s --start-period=30s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Bookline.Api.dll"]
