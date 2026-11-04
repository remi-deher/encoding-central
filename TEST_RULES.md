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
- Réserver les E2E lourds, les scénarios multi-workers complets et les traitements longs aux workflows GitHub Actions adaptés (voir section 8 pour la liste complète des charges lourdes réservées à la CI).
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

## 8. Profil de vérifications locales et charges lourdes réservées à la CI

**Aucun test .NET en local**, y compris les tests ciblés, les projets de test lancés avec `dotnet run` et les tests dans Docker. Tous les tests .NET sont réservés à la CI GitHub Actions. Ne pas les lancer automatiquement ni manuellement sur le poste ; signaler dans le rapport qu'ils n'ont pas été exécutés localement.

**Vérifications légères autorisées en local :** lint et format ciblés, compilation ou typecheck des projets touchés, tests frontend ciblés et E2E légers sans backend .NET. Vérifier d'abord les fichiers modifiés ; utiliser `nice -n 19` pour les commandes coûteuses et ne pas les exécuter en parallèle. Ne jamais lancer `dotnet format` sur la solution entière pendant le travail courant.

**Réservé à la CI :**

- Le workflow `backend-dev.yml` compile avec `-warnaserror` et exécute `ArchitectureTests` et `SqlStatementsPrepareTests` sur les PR et push vers `dev`.
- Tests média de `Encoding.Runtime.Tests` et de `Encoding.Plugins.Tests`.
- Suite backend complète non filtrée (outils média exigés via `ENCODING_TEST_REQUIRE_MEDIA_TOOLS=1`, collecte de couverture, artefacts `.trx`) : job `backend-runtime-tests` de `.github/workflows/backend.yml`.
- Contention de charge `Category=ContentionLoad` : scénario mixte de `LoadContentionTests`, exécuté par `backend-runtime-tests` sur la PR de promotion.
- Tests de stress `Category=Stress` (`LoadStressTests`, variables `ENCODING_STRESS_EXECUTIONS` / `ENCODING_STRESS_NODES` / `ENCODING_STRESS_SECONDS`) : workflow `.github/workflows/stress.yml` (**déclenchement manuel uniquement**, plus de nightly), exclu des PR par le filtre `Category!=Stress`. **CI uniquement, sans exception locale.**
- Tests multi-processus réels (`WorkerProcessLossTests`, `ENCODING_TEST_MULTIPROCESS=1` : vrai serveur, vrais workers, SIGKILL en plein encodage, plusieurs minutes) : job `backend-multiprocess-tests`.
- Tests cluster Docker (`DockerCentralTests`, `ENCODING_TEST_DOCKER=1` + `ENCODING_TEST_CENTRAL_IMAGE` / `ENCODING_TEST_WORKER_IMAGE` construites) : job `backend-docker-cluster-tests`.
- Mesures de charge et de coordination (`MeasuresTwoServerOwnershipLoad`, `ENCODING_TEST_LARGE_COORDINATION_MEASUREMENTS=1`, 7 000 exécutions en file) et toute mesure de performance / benchmark : job `cluster-two-server-measurements` et workflows dédiés.
- Tests natifs des CLI IA (`AiCliNativeCompatibilityTests`, `ENCODING_TEST_AI_CLI_NATIVE=1`) : job `ai-cli-native`.
- Confinement réel du bac à sable (`ProcessSandboxNativeTests` : Landlock, seccomp, réseau TCP/UDP en bouclage, `/proc`) : job `ai-cli-native` avec `ENCODING_TEST_LANDLOCK=1`, qui rend l'indisponibilité du noyau bloquante. Les règles pures (`ProcessSandboxRulesTests`) sont également réservées à la CI.
- E2E lourds, scénarios multi-workers complets et traitements longs : workflows GitHub Actions adaptés.

Les tests conditionnés par les variables `ENCODING_TEST_*` sont ignorés par défaut (xUnit `Skip`) tant que ces variables ne sont pas définies. La CI reste l’autorité pour la couverture des tests .NET.

Le workflow `cluster-two-server-measurements` référence `MeasuresTwoServerOwnershipLoad` (7 000 tâches), mais le test correspondant est absent du code présent. Le filtre CI n'a pas été modifié et cette mesure n'est pas comptée comme une validation disponible.
