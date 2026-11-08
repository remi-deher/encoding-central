#!/usr/bin/env bash
# Copie la sortie `dx build --release` vers dist/ et retire les sections de débogage que dx conserve
# (il passe --debuginfo à wasm-opt). Mêmes fonctionnalités wasm que dx. Variable : WASM_OPT.
set -euo pipefail
crate_dir=$1
rm -rf "$crate_dir/dist"; cp -r "$crate_dir/target/dx/$(basename "$crate_dir")/release/web/public" "$crate_dir/dist"
for w in "$crate_dir"/dist/assets/*.wasm; do
  "$WASM_OPT" -Oz --enable-reference-types --enable-bulk-memory --enable-mutable-globals \
    --enable-nontrapping-float-to-int --enable-sign-ext --strip-debug --strip-producers "$w" -o "$w"
done
