#!/usr/bin/env bash
# Empaquette un prototype wasm (même chaîne pour Leptos et Dioxus) : wasm-bindgen --target web,
# wasm-opt -Oz, index.html minimal. Usage : package.sh <dossier-crate> <nom-binaire> [css...]
# Variables : WASM_BINDGEN, WASM_OPT (chemins des outils), CARGO_TARGET_DIR facultatif.
set -euo pipefail
crate_dir=$1; bin=$2; shift 2
out="$crate_dir/dist"
rm -rf "$out"; mkdir -p "$out"
wasm="$crate_dir/target/wasm32-unknown-unknown/release/$bin.wasm"
"$WASM_BINDGEN" --target web --no-typescript --out-dir "$out" --out-name app "$wasm"
"$WASM_OPT" -Oz --strip-debug --strip-producers --enable-bulk-memory --enable-nontrapping-float-to-int --enable-sign-ext \
  --enable-mutable-globals --enable-reference-types --enable-multivalue \
  "$out/app_bg.wasm" -o "$out/app_bg.wasm"
css_links=""
for css in "$@"; do cp "$css" "$out/"; css_links+="<link rel=\"stylesheet\" href=\"$(basename "$css")\">"; done
cat > "$out/index.html" <<HTML
<!doctype html>
<html lang="fr">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Workers</title>$css_links</head>
<body><script type="module">import init from './app.js'; init();</script></body>
</html>
HTML
