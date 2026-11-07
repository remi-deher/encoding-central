#!/usr/bin/env bash
# Banc de test local uniquement. Ne pas exécuter avec bash -x : sessions privées.
set +x
set -euo pipefail
umask 077

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ACTION="${1:-start}"
GPU="${2:-}"
case "$ACTION" in
    start|stop|restart|status) ;;
    *) printf 'Usage : %s [start|stop|restart|status] [--gpu]\n' "$0" >&2; exit 2 ;;
esac
if (( $# > 2 )) || [[ -n "$GPU" && "$GPU" != --gpu ]]; then
    printf "Seule l'option --gpu est acceptée.\n" >&2
    exit 2
fi

fail() { printf 'Erreur : %s\n' "$*" >&2; exit 1; }
log() { printf '%s\n' "$*"; }
for dependency in docker curl jq git nice; do
    command -v "$dependency" >/dev/null || fail "Dépendance absente : $dependency"
done
docker compose version >/dev/null || fail 'Docker Compose v2 ou supérieur requis.'
docker info >/dev/null 2>&1 || fail 'Le moteur Docker est indisponible.'

# Même version compilée dans les deux cibles ; une image locale par cible et SHA.
SOURCE_SHA="$(git -C "$SCRIPT_DIR" rev-parse HEAD)"
export LOCAL_IMAGE_TAG="sha-$SOURCE_SHA"
export APP_VERSION="0.0.0-dev+${SOURCE_SHA:0:12}"
ENV_FILE="${COMPOSE_ENV_FILE:-$SCRIPT_DIR/.env.test}"
if [[ ! -f "$ENV_FILE" ]]; then
    [[ -z "${COMPOSE_ENV_FILE:-}" ]] || fail 'COMPOSE_ENV_FILE introuvable.'
    ENV_FILE=/dev/null
fi
CENTRAL=(docker compose --env-file "$ENV_FILE" -p encoding-central -f "$SCRIPT_DIR/docker-compose.central.yml")
WORKER=(docker compose --env-file "$ENV_FILE" -p encoding-worker -f "$SCRIPT_DIR/docker-compose.worker.yml")
if [[ "$GPU" == --gpu ]]; then
    WORKER+=(-f "$SCRIPT_DIR/docker-compose.worker-gpu.yml")
fi

TEMP_DIR="$(mktemp -d)"
cleanup() {
    # Les sessions ne survivent pas au script, même après un échec.
    if [[ -f "$TEMP_DIR/cookies" && -n "${CENTRAL_URL:-}" ]]; then
        curl -q --noproxy '*' -fsS --connect-timeout 2 --max-time 5 \
            -b "$TEMP_DIR/cookies" -X POST -H 'X-Encoding-Csrf: 1' \
            "$CENTRAL_URL/api/v1/auth/logout" >/dev/null 2>&1 || true
    fi
    rm -rf -- "$TEMP_DIR"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

stop() {
    # Aucun -v : conserver identités, SQLite et médias synthétiques.
    "${WORKER[@]}" down
    "${CENTRAL[@]}" down
}

api() {
    local method="$1" url="$2"
    local args=(-X "$method" -H 'X-Encoding-Csrf: 1')
    if (( $# == 3 )); then
        args+=(-H 'Content-Type: application/json' --data-binary "@$3")
    fi
    curl -q --noproxy '*' -fsS --connect-timeout 2 --max-time 60 \
        -b "$TEMP_DIR/cookies" -c "$TEMP_DIR/cookies" \
        "${args[@]}" "$url" -o "$TEMP_DIR/response.json" || fail "Échec HTTP : $method $url"
}

wait_ready() {
    local url="$1" attempt
    for ((attempt = 0; attempt < 30; attempt++)); do
        if curl -q --noproxy '*' -fs --connect-timeout 1 --max-time 3 \
            "$url/health/ready" >/dev/null; then
            return
        fi
        sleep 2
    done
    fail "Service indisponible : $url/health/ready"
}

ensure_image() {
    local target="$1" image="$2"
    if docker image inspect "$image" >/dev/null 2>&1; then
        log "Image locale réutilisée : $image"
        return
    fi
    # Ne jamais étiqueter des sources modifiées avec le SHA du dernier commit.
    [[ -z "$(git -C "$SCRIPT_DIR" status --porcelain --untracked-files=no)" ]] \
        || fail 'Commiter les modifications suivies avant de construire une image SHA.'
    [[ -z "$(git -C "$SCRIPT_DIR/.." status --porcelain --untracked-files=normal -- backend/src backend/migrations backend/Dockerfile frontend)" ]] \
        || fail 'Commiter les nouvelles sources avant de construire une image SHA.'
    if [[ "$target" == central ]]; then
        nice -n 19 "${CENTRAL[@]}" --parallel 1 build server
    else
        nice -n 19 "${WORKER[@]}" --parallel 1 build worker
    fi
}

start() {
    log 'Banc local uniquement : compte administrateur générique, ports sur 127.0.0.1.'
    "${CENTRAL[@]}" config --format json > "$TEMP_DIR/central.json"
    "${WORKER[@]}" config --format json > "$TEMP_DIR/worker.json"
    CENTRAL_PORT="$(jq -er '.services.server.ports[0].published' "$TEMP_DIR/central.json")"
    WORKER_PORT="$(jq -er '.services.worker.ports[0].published' "$TEMP_DIR/worker.json")"
    CENTRAL_URL="http://127.0.0.1:$CENTRAL_PORT"
    WORKER_URL="http://127.0.0.1:$WORKER_PORT"
    [[ "$CENTRAL_PORT" != "$WORKER_PORT" ]] || fail 'Les ports du central et du worker doivent différer.'
    if [[ "$GPU" == --gpu ]]; then
        [[ -d /dev/dri ]] || fail '/dev/dri absent : lancer sans --gpu sur cet hôte.'
    fi
    if ! docker network inspect encoding-network >/dev/null 2>&1; then
        docker network create encoding-network >/dev/null
    fi
    ensure_image central "$(jq -er '.services.server.image' "$TEMP_DIR/central.json")"
    ensure_image worker "$(jq -er '.services.worker.image' "$TEMP_DIR/worker.json")"
    "${CENTRAL[@]}" up -d --no-build --pull never
    wait_ready "$CENTRAL_URL"

    # Le mot de passe ne va ni dans le compose, ni dans les arguments de curl.
    # ADMIN_* se règlent dans l'environnement du script, pas dans .env.test.
    export ADMIN_USERNAME="${ADMIN_USERNAME:-admin}"
    export ADMIN_PASSWORD="${ADMIN_PASSWORD:-adminpassword123}"
    api GET "$CENTRAL_URL/api/v1/auth/status"
    if jq -e '.setupRequired == true' "$TEMP_DIR/response.json" >/dev/null; then
        jq -n --slurpfile config "$TEMP_DIR/central.json" \
            '{username: env.ADMIN_USERNAME, password: env.ADMIN_PASSWORD,
              setupToken: $config[0].services.server.environment.Auth__SetupToken,
              workerDisplayName: "encoding-central", centralName: "Central de test local"}' \
            > "$TEMP_DIR/setup.json"
        api POST "$CENTRAL_URL/api/v1/auth/setup" "$TEMP_DIR/setup.json"
    else
        jq -n '{username: env.ADMIN_USERNAME, password: env.ADMIN_PASSWORD}' > "$TEMP_DIR/login.json"
        api POST "$CENTRAL_URL/api/v1/auth/login" "$TEMP_DIR/login.json"
    fi
    api GET "$CENTRAL_URL/api/v1/auth/me"
    jq -e '.role == "admin"' "$TEMP_DIR/response.json" >/dev/null || fail 'Un compte administrateur est requis.'
    api GET "$CENTRAL_URL/api/v1/central"
    CENTRAL_ID="$(jq -er '.id' "$TEMP_DIR/response.json")"

    "${WORKER[@]}" up -d --no-build --pull never
    wait_ready "$WORKER_URL"
    api GET "$WORKER_URL/api/v1/local-worker/status"
    if ! jq -e '.isEnrolled == true' "$TEMP_DIR/response.json" >/dev/null; then
        # Le relais authentifié conserve le secret local dans le conteneur worker.
        jq -n '{apiBaseUrl: "http://encoding-central:8080"}' > "$TEMP_DIR/address.json"
        api POST "$WORKER_URL/api/v1/local-worker/centrals" "$TEMP_DIR/address.json"
        jq -e --arg id "$CENTRAL_ID" '.id == $id' "$TEMP_DIR/response.json" >/dev/null \
            || fail 'Le central découvert ne correspond pas au central local.'
        jq -n --arg id "$CENTRAL_ID" '{centralId: $id, displayName: "encoding-worker"}' > "$TEMP_DIR/enroll.json"
        api POST "$WORKER_URL/api/v1/local-worker/enroll" "$TEMP_DIR/enroll.json"
    fi
    api GET "$WORKER_URL/api/v1/local-worker/status"
    jq -e --arg id "$CENTRAL_ID" '.isEnrolled == true and .centralId == $id' \
        "$TEMP_DIR/response.json" >/dev/null || fail "Le worker n'est pas enrôlé auprès du central local."
    WORKER_ID="$(jq -er '.workerId' "$TEMP_DIR/response.json")"
    # Vérifier aussi le worker intégré : un conteneur démarré ne prouve pas son enrôlement.
    local attempt integrated_id
    for ((attempt = 0; attempt < 30; attempt++)); do
        api GET "$CENTRAL_URL/api/v1/local-worker/status"
        integrated_id="$(jq -r '.workerId' "$TEMP_DIR/response.json")"
        if jq -e --arg id "$CENTRAL_ID" '.isEnrolled == true and .centralId == $id' \
            "$TEMP_DIR/response.json" >/dev/null; then
            api GET "$CENTRAL_URL/api/v1/workers"
            if jq -e --arg external "$WORKER_ID" --arg integrated "$integrated_id" \
                'any(.[]; .id == $external and .status == "online" and .lifecycle == "online")
                 and any(.[]; .id == $integrated and .status == "online" and .lifecycle == "online")' \
                "$TEMP_DIR/response.json" >/dev/null; then
                log "Enrôlement confirmé : worker intégré et worker externe en ligne."
                log "Central : $CENTRAL_URL ; worker : $WORKER_URL"
                return
            fi
        fi
        sleep 2
    done
    fail 'Les deux workers ne sont pas enrôlés et en ligne après le délai prévu.'
}

case "$ACTION" in
    start) start ;;
    stop) stop ;;
    restart) stop; start ;;
    status) "${CENTRAL[@]}" ps; "${WORKER[@]}" ps ;;
esac
