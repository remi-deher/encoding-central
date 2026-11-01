# AGENTS.md

Ce dépôt est la reconstruction d'Encoding autour d'un serveur central. Lire [docs/reconstruction.md](docs/reconstruction.md) avant d'ajouter un domaine.

- Un processus serveur est le seul écrivain de la base SQLite. Pas de cluster, pas de pair, pas de bascule, pas de seconde API dans l'image worker à venir.
- La pile est .NET 10, Angular 22, Material 3 et Docker.
- Une route de gestion est authentifiée par défaut. Ouvrir une route anonyme met à jour le test qui énumère ces routes.
- Les migrations sont additives, à numéro unique, et ne sont plus modifiées une fois appliquées. Toute table nouvelle est dans la base du serveur.
- Le dépôt `remi-deher/encoding-store` reste le catalogue. On ne réécrit pas ses archives publiées.
- Ne pas lancer de suppression ou de remplacement de média.
