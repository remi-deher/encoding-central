# syntax=docker/dockerfile:1
# Deux images : `central` (API, interface web et worker intégré, autonome, seule autorité du cluster) et `worker`
# (agent et interface seulement, sans base de données, toujours relié à un central). Contexte de construction : racine du dépôt.
# Dans `central`, le lanceur démarre et surveille l'API et l'agent ; aucun mode ne se change à chaud.
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
RUN npm run build \
 && npm run build -- --configuration production,worker --output-path dist/worker

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
# Un worker ne fonctionne qu'avec la version exacte du central : la même valeur est compilée dans les deux images.
ARG APP_VERSION=0.0.0-dev
WORKDIR /source/backend

COPY backend/src/Encoding.Api/Encoding.Api.csproj ./src/Encoding.Api/
COPY backend/src/Encoding.WorkerAgent/Encoding.WorkerAgent.csproj ./src/Encoding.WorkerAgent/
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
    dotnet restore ./src/Encoding.WorkerAgent/Encoding.WorkerAgent.csproj; \
    dotnet restore ./src/Encoding.Launcher/Encoding.Launcher.csproj; \
    rm -f /tmp/ca-bundle.pem
COPY backend/migrations/ ./migrations/
COPY backend/src ./src
RUN dotnet publish ./src/Encoding.Api/Encoding.Api.csproj --configuration Release --output /app/api --no-restore -p:InformationalVersion=${APP_VERSION} \
 && dotnet publish ./src/Encoding.WorkerAgent/Encoding.WorkerAgent.csproj --configuration Release --output /app/agent --no-restore -p:InformationalVersion=${APP_VERSION} \
 && dotnet publish ./src/Encoding.Launcher/Encoding.Launcher.csproj --configuration Release --output /app/launcher --no-restore -p:InformationalVersion=${APP_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime-common
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
# Encodage matériel : NVIDIA_* est lu par le NVIDIA Container Toolkit (ignoré sans GPU NVIDIA) ; ENCODING_GPU_DEVICE
# (ex. /dev/dri/renderD129) et LIBVA_DRIVER_NAME (iHD, i965, radeonsi) restent libres pour forcer le choix sur l'hôte.
ENV NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,video,utility
ENV Worker__DataDirectory=/var/lib/encoding-node \
    Runtime__ArtifactDirectory=/var/lib/encoding-node/runtime-artifacts \
    Runtime__CustomPluginDirectory=/opt/encoding/custom-plugins \
    Runtime__ManagedToolDirectory=/opt/encoding/managed-tools \
    Logging__Console__FormatterName=json \
    Logging__Console__FormatterOptions__UseUtcTimestamp=true \
    Logging__Console__FormatterOptions__IncludeScopes=true

FROM runtime-common AS central
COPY --from=ai-cli-build /opt/encoding/ai-cli /opt/encoding/ai-cli
ENV PATH="/opt/encoding/ai-cli/node_modules/.bin:${PATH}"
COPY --from=build /app/api ./api
COPY --from=build /app/agent ./agent
COPY --from=build /app/launcher ./launcher
COPY --from=frontend-build /frontend/dist/frontend/browser ./api/wwwroot
ENV Worker__Roles=server,worker \
    ConnectionStrings__Central="Data Source=/var/lib/encoding-node/cluster.db" \
    Worker__DataDirectory=/var/lib/encoding-node \
    PluginSettings__KeyDirectory=/var/lib/encoding-node/plugin-keys \
    FlowBackups__Directory=/var/lib/encoding-node/flow-backups \
    PluginStore__Directory=/var/lib/encoding-node/plugin-store \
    PluginStore__OfficialRepositoryUrl=https://raw.githubusercontent.com/remi-deher/encoding-store/main/repository.json
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/launcher/Encoding.Launcher.dll"]

FROM runtime-common AS worker
COPY --from=build /app/agent ./agent
COPY --from=frontend-build /frontend/dist/worker/browser ./api/wwwroot
ENV Worker__Roles=worker \
    Worker__WorkerImage=true \
    WorkerUi__Expose=true \
    PluginSettings__KeyDirectory=/var/lib/encoding-node/plugin-keys \
    PluginStore__Directory=/var/lib/encoding-node/plugin-store
EXPOSE 8081
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/agent/Encoding.WorkerAgent.dll"]
