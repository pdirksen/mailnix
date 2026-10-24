# syntax=docker/dockerfile:1
# Build from the repository root:  docker build -f docker/Dockerfile -t mailnix .

# --- Frontend: type-check, build, license gate, third-party notices -------------------------------
FROM node:24-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build \
 && npm run license:check \
 && npm run license:notices

# --- Backend: restore (locked), publish with the SPA in wwwroot -----------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src/backend
COPY backend/global.json backend/Directory.Build.props backend/Directory.Packages.props ./
COPY backend/src/Mailnix.Api/Mailnix.Api.csproj backend/src/Mailnix.Api/packages.lock.json src/Mailnix.Api/
COPY backend/src/Mailnix.Infrastructure/Mailnix.Infrastructure.csproj backend/src/Mailnix.Infrastructure/packages.lock.json src/Mailnix.Infrastructure/
RUN dotnet restore src/Mailnix.Api/Mailnix.Api.csproj --locked-mode
COPY backend/ ./
COPY --from=frontend /src/frontend/dist/ src/Mailnix.Api/wwwroot/
# The OpenAPI document is generated during development builds only.
RUN dotnet publish src/Mailnix.Api/Mailnix.Api.csproj -c Release -o /app --no-restore -p:OpenApiGenerateDocuments=false

# --- Runtime ---------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN mkdir /data && chown app:app /data
COPY --from=backend /app ./
COPY LICENSE ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Mailnix__Database__ConnectionString="Data Source=/data/mailnix.db"
VOLUME /data
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Mailnix.Api.dll"]
