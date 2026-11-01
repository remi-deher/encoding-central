#!/usr/bin/env bash
# Tests locaux via le démon Docker : la suite du serveur dans le SDK, puis l'image réelle.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"

if ! docker info >/dev/null 2>&1; then
  echo "Docker n'est pas joignable. Le compte est peut-être dans le groupe docker sans que la session l'ait pris : newgrp docker" >&2
  exit 1
fi

echo "Tests du serveur dans mcr.microsoft.com/dotnet/sdk:10.0"
docker run --rm \
  --user "$(id -u):$(id -g)" \
  -e HOME=/tmp/home \
  -e DOTNET_CLI_HOME=/tmp/home/.dotnet \
  -e NUGET_PACKAGES=/tmp/nuget \
  -v "$root":/src \
  -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test backend/Encoding.Central.slnx --configuration Release

image=encoding-central:test
name="encoding-central-test-$$"
cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "Construction de l'image"
docker build --tag "$image" "$root"
docker run --detach --name "$name" --publish 127.0.0.1::8080 "$image" >/dev/null
port=$(docker port "$name" 8080 | awk -F: 'NR == 1 { print $NF }')
base="http://127.0.0.1:${port}"

ready=0
for _ in $(seq 1 60); do
  if curl --silent --fail --show-error "$base/health/ready" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
if [[ "$ready" != 1 ]]; then
  echo "Le conteneur n'est pas devenu prêt." >&2
  docker logs "$name" >&2 || true
  exit 1
fi

runtime_uid=$(docker exec "$name" id -u)
if [[ "$runtime_uid" == 0 ]]; then
  echo "Le processus du serveur tourne en root." >&2
  exit 1
fi

python3 - "$base" <<'PY'
import json
import sys
import urllib.error
import urllib.request

base = sys.argv[1]
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor())

def call(method, path, body=None, expected=None):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(base + path, data=data, method=method)
    if body is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with opener.open(request) as response:
            status, payload = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, payload = error.code, error.read()
    if expected is not None and status != expected:
        raise SystemExit(f"{method} {path} -> {status}, attendu {expected}: {payload[:400]!r}")
    if b"at Encoding." in payload or b"Exception" in payload:
        raise SystemExit(f"{method} {path} expose une exception: {payload[:400]!r}")
    if not payload:
        return None
    try:
        return json.loads(payload)
    except json.JSONDecodeError:
        return payload

page = call("GET", "/", expected=200)
if b"<app-root" not in page:
    raise SystemExit("L'interface n'est pas servie.")
call("GET", "/health/live", expected=200)
call("GET", "/health/ready", expected=200)
call("GET", "/api/v1/server", expected=401)
call("GET", "/api/v1/workers", expected=401)
status = call("GET", "/api/v1/setup", expected=200)
if status.get("setupRequired") is not True:
    raise SystemExit(f"Mise en service inattendue: {status}")

account = {
    "username": "remi",
    "password": "phrase-de-passe-12",
    "displayName": "Maison",
    "publicBaseUrl": "http://127.0.0.1:8080",
}
created = call("POST", "/api/v1/setup", account, expected=201)
if created["server"]["publicBaseUrl"] != "http://127.0.0.1:8080":
    raise SystemExit(f"Adresse publique inattendue: {created}")
if call("GET", "/api/v1/workers", expected=200) != []:
    raise SystemExit("Le registre des workers n'est pas vide.")
call("POST", "/api/v1/setup", account, expected=409)

leaked = "secret-value-should-not-leak"
refused = call("POST", "/api/v1/session", {"username": "remi", "password": leaked}, expected=401)
if leaked.encode() in json.dumps(refused).encode():
    raise SystemExit("Le refus de connexion renvoie le mot de passe.")
call("POST", "/api/v1/session", {"username": "remi", "password": account["password"]}, expected=200)
call("DELETE", "/api/v1/session", expected=204)
call("GET", "/api/v1/server", expected=401)
print("image ok", base)
PY
