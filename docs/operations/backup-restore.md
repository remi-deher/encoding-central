# Sauvegarde et restauration

## Mode pair à pair

Le serveur utilise exclusivement SQLite local et le maillage signé. Avant la mise à jour d’une ancienne installation utilisant le mode de consensus retiré, sauvegarder à froid ses bases, identités, clés et journaux. Ne pas démarrer ses projections indépendamment : aucune conversion automatique vers le maillage n’est prise en charge.

## Ce qui doit être sauvegardé

| Donnée | Emplacement (Compose) | Criticité |
| --- | --- | --- |
| Base SQLite locale : configuration, flows, bibliothèques, membres et historique réconcilié | `/var/lib/encoding-node/cluster.db`, volume `encoding-node-data` | **Indispensable sur chaque serveur** |
| Clés Data Protection des plugins | volume `encoding-plugin-keys` (`PluginSettings__KeyDirectory`) | **Indispensable.** À sauvegarder avec la base : elles servent à déchiffrer les secrets de plugins. |
| Sauvegardes JSON des flows | volume `encoding-flow-backups` (`FlowBackups__Directory`) | Recommandé. Une copie indépendante de la base. |
| Artefacts d’exécution | volume `encoding-runtime-artifacts` (`Runtime__ArtifactDirectory`) | Facultatif pour les tâches terminées ; requis pour préserver les reprises d’étapes qui en dépendent. |
| Identité locale du nœud | volume `encoding-node-data` | À conserver pour éviter un nouvel enrôlement. |
| Fichiers média publiés | bibliothèques média | À sauvegarder selon la politique du stockage média. |

Chaque serveur conserve sa propre base. Sauvegarder la base de chacun ; la réconciliation n’est ni une sauvegarde ni une copie des fichiers médias et artefacts.

## Sauvegarde à froid

Arrêter le conteneur pour vider le WAL SQLite, puis archiver le volume de données et les volumes séparés utiles :

```bash
docker compose stop encoding
for volume in encoding-node-data encoding-plugin-keys encoding-flow-backups encoding-runtime-artifacts; do
  docker run --rm -v "backend_${volume}:/data:ro" -v "$PWD:/out" alpine \
    tar czf "/out/${volume}-$(date -u +%Y%m%dT%H%M%SZ).tgz" -C /data .
done
docker compose start encoding
```

Le préfixe `backend_` correspond au nom de projet Compose de cet exemple ; vérifier le nom réel avec `docker volume ls`. Conserver une copie chiffrée hors de l’hôte, surtout pour les clés de plugins.

Pour une sauvegarde à chaud, utiliser `sqlite3` sur le fichier monté dans un chemin local et `.backup destination.db`, qui inclut les changements du WAL. Ne jamais copier uniquement `cluster.db` pendant que le serveur tourne.

## Vérifier et restaurer

Restaurer périodiquement une sauvegarde dans un volume de test puis démarrer une instance isolée avec ce volume. Contrôler `/health/details`, les flows, les utilisateurs, les bibliothèques et les événements d’exécution avant de déclarer la sauvegarde exploitable.

Pour une restauration réelle :

1. Arrêter le serveur concerné et conserver une copie de ses volumes actuels.
2. Restaurer ensemble `encoding-node-data` et `encoding-plugin-keys`. Restaurer également les volumes de sauvegarde de flows et d’artefacts si nécessaire.
3. Démarrer le serveur et contrôler `GET /health/details` : `database`, `migrations` et les contrôles de stockage doivent être `healthy`.
4. Vérifier la page Cluster et laisser les pairs se réconcilier. Les données réconciliables plus récentes peuvent revenir depuis un autre nœud ; les états locaux, baux actifs et fichiers ne sont pas récupérés par le maillage.

Si un média a été remplacé, sa sauvegarde de remplacement doit également être restaurée depuis le stockage configuré pour cette bibliothèque.

## Installation fraîche

Au premier démarrage, le serveur crée `/var/lib/encoding-node/cluster.db`, applique les migrations et établit l’identité locale du cluster. Pour installer sans reprendre les données précédentes, démarrer avec un volume `encoding-node-data` neuf. Cela réinitialise aussi l’identité du nœud ; les volumes de sauvegardes, de clés de plugins et d’artefacts sont distincts et doivent être conservés ou remplacés selon le besoin.

## Perte d’un worker ou d’un serveur

Retirer une identité de worker perdue depuis l’interface, puis enrôler la nouvelle installation et redéclarer ses accès aux bibliothèques. Les tentatives actives d’un worker perdu échouent après expiration du bail ; elles ne sont pas reprises automatiquement. Une relance vient d’une règle explicite du flow ou d’une action manuelle.

À la perte d’un serveur, un autre pair conserve les états réconciliés. Restaurer le volume du nœud perdu si son état local doit être récupéré ; réenrôler l’installation si l’identité de nœud a été perdue. Les journaux du maillage ne remplacent pas les sauvegardes.

## Mise à jour

Avant une mise à jour qui apporte des migrations, sauvegarder chaque fichier SQLite et les clés de plugins. Les migrations ne sont pas réversibles : un retour arrière exige de restaurer la base et les volumes correspondants. Les migrations sont appliquées dans une transaction SQLite d’écriture immédiate.
