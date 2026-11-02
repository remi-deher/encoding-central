#!/usr/bin/env bash
# Installe des paquets apt sur un runner GitHub sans rester bloqué sur un miroir lent :
# les .deb déjà en cache (APT_DEBS_DIR) sont réutilisés, apt reprend les téléchargements interrompus,
# jusqu'à 3 essais bornés à 4 minutes, et à partir du 2e essai on bascule sur archive.ubuntu.com.
set -euo pipefail
debs_dir="${APT_DEBS_DIR:-}"
options=(-o Acquire::Retries=3 -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20 -o Dpkg::Use-Pty=0)

if [ -n "$debs_dir" ] && compgen -G "$debs_dir/*.deb" > /dev/null; then
  sudo cp -n "$debs_dir"/*.deb /var/cache/apt/archives/
  echo "$(ls "$debs_dir"/*.deb | wc -l) paquets repris du cache."
fi

for attempt in 1 2 3; do
  if [ "$attempt" -gt 1 ]; then
    # Miroir de secours : le miroir Azure des runners est parfois très lent.
    sudo sed -i -E 's#(mirror\+file:/etc/apt/apt-mirrors\.txt|https?://azure\.archive\.ubuntu\.com/ubuntu/?)#http://archive.ubuntu.com/ubuntu/#' /etc/apt/sources.list.d/*.sources /etc/apt/sources.list 2>/dev/null || true
  fi
  if sudo timeout 240 apt-get "${options[@]}" update && sudo timeout 240 apt-get "${options[@]}" install -y --no-install-recommends "$@"; then
    if [ -n "$debs_dir" ]; then
      mkdir -p "$debs_dir"
      sudo cp -n /var/cache/apt/archives/*.deb "$debs_dir"/ 2>/dev/null || true
      sudo chown -R "$(id -u):$(id -g)" "$debs_dir"
    fi
    exit 0
  fi
  echo "apt a échoué ou dépassé 4 minutes (essai $attempt/3)." >&2
  sleep 5
done
exit 1
