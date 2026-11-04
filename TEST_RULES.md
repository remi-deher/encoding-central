# Règles de tests et d’intégration continue

Ces règles sont obligatoires pour les changements du projet. Les tests doivent protéger les comportements métier et les garanties du central, tout en restant rapides à exécuter pendant le développement.

## 1. CI GitHub Actions

- Mettre en place GitHub Actions pour exécuter les vérifications du projet sur les changements proposés et poussés.
- La CI doit au minimum installer les dépendances, vérifier le format et le lint, compiler ou vérifier les types, puis exécuter les tests adaptés au changement.
- Les vérifications requises doivent être visibles et réussir avant qu’un changement puisse être intégré.
- Les contrôles locaux et ceux de GitHub doivent utiliser les mêmes commandes et configurations lorsque c’est possible.
- Les jobs doivent échouer clairement, conserver les diagnostics utiles et avoir des limites de durée explicites.

## 2. Tests des fonctionnalités

- Toute fonctionnalité ou correction doit être accompagnée de tests couvrant son comportement attendu et ses cas d’échec importants.
- Tester les règles métier et les contrats observables plutôt que les détails internes d’implémentation.
- Ajouter des tests unitaires rapides pour les règles déterministes et les fonctions métier isolées.
- Ajouter des tests d’intégration pour les interactions importantes avec le stockage, la file de tâches, le système de fichiers, les outils média et les plugins.
- Pour un changement d’API ou de contrat, vérifier la compatibilité entre les composants concernés.

## 3. Tests propres au central et aux médias

- Couvrir l’éligibilité des workers selon leurs capacités et leurs accès configurés aux bibliothèques.
- Couvrir le mode d’accès direct, le mode non pris en charge et le transfert WebDAV vers un worker lorsque ces capacités sont implémentées.
- Couvrir les baux de tâches expirés, la reprise après perte d’un worker, les doublons et l’exécution concurrente.
- Vérifier qu’un seul traitement modifie un média donné à la fois et qu’une sortie invalide ne remplace jamais la source.
- Utiliser des médias de test petits, reproductibles et distribuables légalement ; préférer des fichiers synthétiques lorsque c’est pertinent.
- Ne jamais utiliser une bibliothèque personnelle ou un média utilisateur comme dépendance d’un test automatisé.

## 4. E2E

- Prévoir un parcours E2E léger, déterministe et exécutable localement pour vérifier le chemin critique de l’application.
- Réserver les E2E lourds, les scénarios multi-workers complets et les traitements longs aux workflows GitHub Actions adaptés.
- Garder les E2E indépendants des services personnels, des secrets locaux et des médias non versionnés.
- En cas d’échec, fournir des journaux et des artefacts de diagnostic suffisants pour reproduire le problème.

## 5. Code mort et qualité du code

- Ajouter des vérifications automatisées qui détectent le code mort ou inutilisé selon les outils retenus par le projet.
- Exécuter le lint localement et dans GitHub Actions avec la même configuration.
- Vérifier également le formatage, la compilation et les types statiques disponibles dans la stack choisie.
- Ne pas supprimer du code sur la seule base d’un outil de détection : vérifier ses usages dynamiques, ses points d’entrée et ses extensions par plugins.

## 6. CSS et interface

- Vérifier automatiquement le lint et la compilation des styles.
- Détecter les sélecteurs ou règles CSS inutilisés avec un outil adapté, en tenant compte des classes dynamiques et des extensions.
- Vérifier les changements visuels concernés sur les tailles ordinateur et mobile ; privilégier des captures ou comparaisons visuelles ciblées lorsque cela apporte une couverture utile.
- Ajouter des vérifications d’accessibilité automatisables aux parcours UI importants.

## 7. Fiabilité des tests

- Les tests doivent isoler leurs données et nettoyer leurs fichiers temporaires sans toucher aux médias réels.
- Éviter les dépendances aux horaires, à l’ordre d’exécution, au réseau public et aux ressources partagées non contrôlées.
- Ne pas ignorer durablement un test instable : identifier sa cause, le corriger ou suivre explicitement sa mise en quarantaine avec une action de résolution.
- Ne pas abaisser une vérification ou masquer un échec pour faire passer la CI ; documenter toute exception temporaire et sa portée.
- Mesurer la couverture comme un indicateur, pas comme un objectif isolé : protéger d’abord les comportements et invariants à risque.

## 8. Profil de tests .NET local

Pour le développement courant, lancer les scénarios Launcher et Plugins, puis cibler dans Runtime les classes qui couvrent le comportement modifié :

```sh
dotnet run --project backend/tests/Encoding.Launcher.Tests --configuration Release
dotnet run --project backend/tests/Encoding.Plugins.Tests --configuration Release
dotnet test backend/tests/Encoding.Runtime.Tests --configuration Release --filter "FullyQualifiedName~ExecutionStatesTests"
```

Remplacer `ExecutionStatesTests` par la classe concernée lorsqu'une autre couverture ciblée est adaptée. Pour une modification du runtime et avant une PR, lancer toute la suite Runtime hors stress avec le filtre utilisé par la CI :

```sh
dotnet test backend/tests/Encoding.Runtime.Tests --configuration Release --filter "Category!=Stress"
```

Les tests `LoadContentionTests` restent dans cette suite hors stress. `WorkerProcessLossTests`, `DockerCentralTests` et `LoadStressTests` sont réservés à la CI par défaut; une exécution locale ponctuelle reste possible si le développeur la demande et dispose des prérequis. Ne pas définir leurs variables `ENCODING_TEST_MULTIPROCESS`, `ENCODING_TEST_DOCKER` ou le filtre `Category=Stress` dans le profil courant. Le stress utilise par défaut 20 000 tâches, 30 nœuds et 60 secondes.

Le workflow `cluster-two-server-measurements` référence aussi `MeasuresTwoServerOwnershipLoad`, mais cette méthode/test est absente du code présent. Ne pas présenter cette mesure comme une vérification locale disponible ni modifier son filtre pour produire un résultat artificiel; traiter la divergence séparément.
