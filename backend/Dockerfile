# syntax=docker/dockerfile:1
# Image unique : serveur (API et interface web), worker et lanceur. Contexte de construction : racine du dépôt.
# Le lanceur démarre et arrête le serveur et l'agent selon le mode du nœud (architecture 0036).
ARG DOTNET_VERSION=10.0

FROM node:24-bookworm-slim AS ai-cli-build
ARG CODEX_CLI_VERSION=0.163.0
ARG CLAUDE_CODE_VERSION=2.1.302
RUN --mount=type=secret,id=proxy_ca,required=false \
    if [ -f /run/secrets/proxy_ca ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/proxy_ca; fi; \
    npm install --prefix /opt/encoding/ai-cli --omit=dev --no-audit --no-fund \
      @openai/codex@${CODEX_CLI_VERSION} @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}

FROM node:24-alpine AS frontend-build
WORKDIR /frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm \
    --mount=type=secret,id=proxy_ca,required=false \
    if [ -f /run/secrets/proxy_ca ]; then \
      NODE_EXTRA_CA_CERTS=/run/secrets/proxy_ca npm ci --strict-ssl=true; \
    else \
      npm ci --strict-ssl=true; \
    fi
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /source/backend

COPY backend/src/Encoding.Api/Encoding.Api.csproj ./src/Encoding.Api/
COPY backend/src/Encoding.NodeAgent/Encoding.NodeAgent.csproj ./src/Encoding.NodeAgent/
COPY backend/src/Encoding.Launcher/Encoding.Launcher.csproj ./src/Encoding.Launcher/
COPY backend/src/Encoding.Contracts/Encoding.Contracts.csproj ./src/Encoding.Contracts/
COPY backend/src/Encoding.Core/Encoding.Core.csproj ./src/Encoding.Core/
RUN --mount=type=cache,target=/root/.local/share/NuGet/http-cache \
    --mount=type=secret,id=proxy_ca,required=false \
    set -eu; \
    if [ -f /run/secrets/proxy_ca ]; then \
      cat /etc/ssl/certs/ca-certificates.crt /run/secrets/proxy_ca > /tmp/ca-bundle.pem; \
      export SSL_CERT_FILE=/tmp/ca-bundle.pem; \
    fi; \
    dotnet restore ./src/Encoding.Api/Encoding.Api.csproj; \
    dotnet restore ./src/Encoding.NodeAgent/Encoding.NodeAgent.csproj; \
    dotnet restore ./src/Encoding.Launcher/Encoding.Launcher.csproj; \
    rm -f /tmp/ca-bundle.pem
COPY backend/migrations/ ./migrations/
COPY backend/src ./src
RUN dotnet publish ./src/Encoding.Api/Encoding.Api.csproj --configuration Release --output /app/api --no-restore \
 && dotnet publish ./src/Encoding.NodeAgent/Encoding.NodeAgent.csproj --configuration Release --output /app/agent --no-restore \
 && dotnet publish ./src/Encoding.Launcher/Encoding.Launcher.csproj --configuration Release --output /app/launcher --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
COPY --from=ai-cli-build /opt/encoding/ai-cli /opt/encoding/ai-cli
ENV PATH="/opt/encoding/ai-cli/node_modules/.bin:${PATH}"
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt/lists,sharing=locked \
    --mount=type=secret,id=proxy_ca,required=false \
    set -eu; \
    ca_opt=''; \
    if [ -f /run/secrets/proxy_ca ]; then \
      cat /etc/ssl/certs/ca-certificates.crt /run/secrets/proxy_ca > /tmp/ca-bundle.pem; \
      ca_opt='-o Acquire::https::CaInfo=/tmp/ca-bundle.pem'; \
    fi; \
    apt-get $ca_opt update && apt-get $ca_opt install -y --no-install-recommends ffmpeg mkvtoolnix mediainfo imagemagick nodejs python3 hwdata; \
    ffmpeg -hide_banner -encoders 2>/dev/null | grep -q nvenc; \
    apt-get $ca_opt purge -y ffmpeg mkvtoolnix mediainfo imagemagick imagemagick-6.q16; \
    rm -f /tmp/ca-bundle.pem
# Encodage matériel (docs/architecture/0051-hardware-encoding-in-image.md).
# Intel : pilote iHD « non-free » (multiverse) pour VAAPI, repli sur la version libre ; libvpl2 + libmfx-gen1.2 (QSV Gen12+),
# libmfx1 (Media SDK, QSV Gen9.5 comme l'UHD 630) et vainfo pour le diagnostic. AMD : Mesa VA-API (pas d'AMF).
# NVIDIA : le pilote propriétaire vient de l'hôte par le NVIDIA Container Toolkit (rien à installer ici).
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt/lists,sharing=locked \
    --mount=type=secret,id=proxy_ca,required=false \
    set -eu; \
    ca_opt=''; \
    if [ -f /run/secrets/proxy_ca ]; then \
      cat /etc/ssl/certs/ca-certificates.crt /run/secrets/proxy_ca > /tmp/ca-bundle.pem; \
      ca_opt='-o Acquire::https::CaInfo=/tmp/ca-bundle.pem'; \
    fi; \
    sed -i 's/^Components: main restricted universe$/Components: main restricted universe multiverse/' /etc/apt/sources.list.d/ubuntu.sources 2>/dev/null || true; \
    apt-get $ca_opt update; \
    (apt-get $ca_opt install -y --no-install-recommends intel-media-va-driver-non-free \
     || apt-get $ca_opt install -y --no-install-recommends intel-media-va-driver); \
    apt-get $ca_opt install -y --no-install-recommends libvpl2 libmfx1 libmfx-gen1.2 vainfo mesa-va-drivers libdrm-amdgpu1; \
    rm -f /tmp/ca-bundle.pem
# Plugins et outils du Store téléchargés par les workers, hors du dossier privé de données du nœud.
RUN mkdir -p /opt/encoding/custom-plugins /opt/encoding/managed-tools /var/lib/encoding-node /var/lib/encoding/flow-backups /var/lib/encoding/runtime-artifacts /var/lib/encoding/plugin-keys \
 && chown -R $APP_UID:$APP_UID /opt/encoding/custom-plugins /opt/encoding/managed-tools /var/lib/encoding-node /var/lib/encoding
WORKDIR /app
COPY --from=build /app/api ./api
COPY --from=build /app/agent ./agent
COPY --from=build /app/launcher ./launcher
COPY --from=frontend-build /frontend/dist/frontend/browser ./api/wwwroot
# Mode initial du nœud : serveur et worker. Un mode enregistré dans le dossier de données le remplace.
# Base unique : SQLite local par nœud (architectures 0043/0049).
# Encodage matériel : NVIDIA_* est lu par le NVIDIA Container Toolkit (ignoré sans GPU NVIDIA) ; ENCODING_GPU_DEVICE
# (ex. /dev/dri/renderD129) et LIBVA_DRIVER_NAME (iHD, i965, radeonsi) restent libres pour forcer le choix sur l'hôte.
ENV NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,video,utility
ENV Node__Roles=server,worker \
    ConnectionStrings__Cluster="Data Source=/var/lib/encoding-node/cluster.db" \
    Node__DataDirectory=/var/lib/encoding-node \
    PluginSettings__KeyDirectory=/var/lib/encoding-node/plugin-keys \
    FlowBackups__Directory=/var/lib/encoding-node/flow-backups \
    Runtime__ArtifactDirectory=/var/lib/encoding-node/runtime-artifacts \
    Runtime__CustomPluginDirectory=/opt/encoding/custom-plugins \
    PluginStore__Directory=/var/lib/encoding-node/plugin-store \
    PluginStore__OfficialRepositoryUrl=https://raw.githubusercontent.com/remi-deher/encoding-store/main/repository.json \
    Logging__Console__FormatterName=json \
    Logging__Console__FormatterOptions__UseUtcTimestamp=true \
    Logging__Console__FormatterOptions__IncludeScopes=true
# 8080 : API, interface et échanges signés du maillage.
# L'agent (8081) et le lanceur (8082) restent locaux au conteneur.
EXPOSE 8080
# Exécution sans root ($APP_UID, 1654 dans l'image de base). GPU : ajouter le GID du groupe de renderD128 au conteneur
# (group_add, voir docker-compose.gpu.yml). Les volumes déjà créés par une ancienne image (root) doivent être rendus
# à cet utilisateur : voir docs/operations/reverse-proxy.md.
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/launcher/Encoding.Launcher.dll"]
