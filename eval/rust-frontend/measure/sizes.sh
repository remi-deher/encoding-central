#!/usr/bin/env bash
# Tailles brute / gzip -9 / brotli -q 11 de chaque fichier d'un dossier dist, et total.
set -euo pipefail
dir=$1; tr=0; tg=0; tb=0
printf '%-48s %10s %10s %10s\n' fichier brut gzip brotli
while IFS= read -r f; do
  r=$(stat -c %s "$f"); g=$(gzip -9c "$f" | wc -c); b=$(brotli -q 11 -c "$f" | wc -c)
  tr=$((tr+r)); tg=$((tg+g)); tb=$((tb+b))
  printf '%-48s %10d %10d %10d\n' "${f#$dir/}" "$r" "$g" "$b"
done < <(find "$dir" -type f \( -name '*.js' -o -name '*.wasm' -o -name '*.css' -o -name '*.html' \) | sort)
printf '%-48s %10d %10d %10d\n' TOTAL "$tr" "$tg" "$tb"
