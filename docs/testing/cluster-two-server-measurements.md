# Mesure de coordination à deux serveurs

Exécution locale du test `ClusterTaskTransferTests.MeasuresTwoServerOwnershipLoad`, avec `ENCODING_TEST_LARGE_COORDINATION_MEASUREMENTS=1` : 7 000 tâches, deux SQLite distincts, deux serveurs logiques **dans un même processus**, deux workers synthétiques, claims/renouvellements et lectures paginées en concurrence avec capture incrémentale et réplication.

| Mesure | Résultat local du code livré (`0e07a56`) |
|---|---:|
| Mise en place | 3 309 ms |
| Exercice | 12 526 ms |
| Claims p95 / p99 | 310 / 523 ms |
| Renouvellements p95 / p99 | 73 / 82 ms |
| Vue générale p95 / historique p95 | 222 / 150 ms |
| Admissions écrivain A / B | 281 / 286 |
| Rejets / expirations écrivain | 0 / 0 |

Cette mesure n’est ni un SLO ni une validation sur deux machines physiques, NAS, coupures réseau réelles ou 7 000 encodages. Mesure finale réussie : [données brutes JSON](cluster-two-server-measurements.json). Les métriques d’activité disque/NAS restent non mesurées. Le job CI `cluster-two-server-measurements` rejoue ce scénario et conserve journal et TRX ; il vérifie l’achèvement et les invariants, sans imposer un seuil de latence dépendant de la machine. Le scénario média distinct teste réellement encodage FFmpeg, transfert, remux et publication sur fichiers synthétiques.

Commande :

```sh
ENCODING_TEST_LARGE_COORDINATION_MEASUREMENTS=1 dotnet test backend/tests/Encoding.Runtime.Tests -c Release --no-build --filter FullyQualifiedName~MeasuresTwoServerOwnershipLoad --logger 'console;verbosity=detailed'
```

Le test capture au moins une passe de réplication incrémentale réellement concurrente. Ce volume de 7 000 tâches en file n’équivaut pas à 7 000 traitements exécutés : 100 claims/renouvellements, 100 lectures de vue générale et 100 lectures d’historique sont mesurés. Les admissions de l’écrivain ont zéro rejet et zéro expiration. Les durées sont des observations locales uniques, pas une comparaison statistique entre versions.
