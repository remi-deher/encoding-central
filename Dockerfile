# syntax=docker/dockerfile:1
# Image du serveur central : API et interface. Pas d'agent, pas de second rôle.
ARG DOTNET_VERSION=10.0

FROM node:24-alpine AS frontend-build
WORKDIR /frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /source
COPY backend/Directory.Build.props backend/Directory.Build.props
COPY backend/src/Encoding.Server/Encoding.Server.csproj backend/src/Encoding.Server/
RUN dotnet restore backend/src/Encoding.Server/Encoding.Server.csproj
COPY backend/src/Encoding.Server/ backend/src/Encoding.Server/
RUN dotnet publish backend/src/Encoding.Server/Encoding.Server.csproj --configuration Release --output /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
RUN mkdir -p /var/lib/encoding && chown "$APP_UID:$APP_UID" /var/lib/encoding
WORKDIR /app
COPY --from=build /app ./
COPY --from=frontend-build /frontend/dist/frontend/browser ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080 \
    Database__Path=/var/lib/encoding/server.db \
    Logging__Console__FormatterName=json
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Encoding.Server.dll"]
