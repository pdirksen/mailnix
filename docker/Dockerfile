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
# Analyzer settings (warnings are errors; generated migrations are exempt).
COPY .editorconfig /src/
COPY backend/global.json backend/Directory.Build.props backend/Directory.Packages.props ./
# Every project the API references, so restore can be cached independently of source changes.
COPY backend/src/Mailnix.Api/Mailnix.Api.csproj backend/src/Mailnix.Api/packages.lock.json src/Mailnix.Api/
COPY backend/src/Mailnix.Core/Mailnix.Core.csproj backend/src/Mailnix.Core/packages.lock.json src/Mailnix.Core/
COPY backend/src/Mailnix.Infrastructure/Mailnix.Infrastructure.csproj backend/src/Mailnix.Infrastructure/packages.lock.json src/Mailnix.Infrastructure/
COPY backend/src/Mailnix.Mail/Mailnix.Mail.csproj backend/src/Mailnix.Mail/packages.lock.json src/Mailnix.Mail/
COPY backend/src/Mailnix.Worker/Mailnix.Worker.csproj backend/src/Mailnix.Worker/packages.lock.json src/Mailnix.Worker/
RUN dotnet restore src/Mailnix.Api/Mailnix.Api.csproj --locked-mode
COPY backend/ ./
COPY --from=frontend /src/frontend/dist/ src/Mailnix.Api/wwwroot/
# Release images: --build-arg MailnixVersion=1.4.0 (without it, the image is a development build).
ARG MailnixVersion
# The OpenAPI document is generated during development builds only.
RUN dotnet publish src/Mailnix.Api/Mailnix.Api.csproj -c Release -o /app --no-restore -p:OpenApiGenerateDocuments=false

# --- Runtime ---------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG MailnixVersion=0.0.0-dev
LABEL org.opencontainers.image.version=$MailnixVersion
WORKDIR /app
RUN mkdir /data && chown app:app /data
COPY --from=backend /app ./
COPY LICENSE ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Mailnix__DataDirectory=/data
VOLUME /data
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Mailnix.Api.dll"]
