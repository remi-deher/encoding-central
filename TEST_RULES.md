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

Pour le backend .NET, le profil courant exécute les scénarios Launcher et Plugins et cible dans Runtime les tests liés au changement. Pour les changements Runtime et avant une PR, élargir une fois à toute la suite Runtime hors Stress et contention de charge. Les tests Stress restent strictement réservés à la CI. Les clusters multi-processus et Docker restent réservés à la CI par défaut, avec une exécution locale ponctuelle uniquement si elle est explicitement demandée et que les prérequis sont disponibles.

**Léger — autorisé en local :**

- Lint et format : `npm run lint`, `npm run format:flows`, `npm run format:settings` (frontend).
- Compilation / typecheck : `dotnet build <projet.csproj> --no-restore` sur les projets touchés (avec `-warnaserror` et le `.editorconfig` racine ; la solution entière uniquement dans les cas d’élargissement ci-dessous), `npm run build`.
- Tests unitaires ciblés et rapides : `npm test` et `node --test tests/*.test.mjs` (frontend). Pour le backend, le profil .NET local est :

  ```sh
  nice -n 19 dotnet run --project backend/tests/Encoding.Launcher.Tests --configuration Release
  nice -n 19 dotnet run --project backend/tests/Encoding.Plugins.Tests --configuration Release
  nice -n 19 dotnet test backend/tests/Encoding.Runtime.Tests --configuration Release --filter "FullyQualifiedName~ExecutionStatesTests&Category!=Stress&Category!=ContentionLoad"
  ```

  Les tests média (ffmpeg, ffprobe, MKVToolNix, scripts) sont **délégués à la CI** : en local, définir `ENCODING_TEST_SKIP_MEDIA=1` devant les commandes Plugins et Runtime (`ENCODING_TEST_SKIP_MEDIA=1 nice -n 19 dotnet run --project backend/tests/Encoding.Plugins.Tests --configuration Release`). La CI ne le définit jamais et `ENCODING_TEST_REQUIRE_MEDIA_TOOLS=1` l'emporte.

  Remplacer `ExecutionStatesTests` par la classe liée au comportement modifié.
- E2E léger : `npm run test:e2e` (parcours Playwright smoke, API simulée, sans backend ni média réel).

**Périmètre minimal — économie CPU (backend .NET et frontend Angular) :** vérifier d’abord **uniquement les fichiers modifiés ou créés**. Liste : `git diff --name-only HEAD` plus `git ls-files --others --exclude-standard`.

1. **Format** : jamais `dotnet format` sur `Encoding.sln` pendant le travail courant. Utiliser `dotnet format <projet.csproj> --verify-no-changes --no-restore --include <fichiers modifiés>`.
2. **Build** : seulement les projets touchés, `dotnet build <projet.csproj> --no-restore`, pas la solution.
3. **Tests** : d’abord ceux liés aux fichiers modifiés, `dotnet test <projet.Tests.csproj> --no-build --filter "FullyQualifiedName~<Classe ou namespace>&Category!=Stress&Category!=ContentionLoad"`.
4. **Élargissement** (projet entier, puis solution) seulement si : un contrat partagé est modifié (interface, DTO, classe de base, configuration commune, `Directory.Build.props`, package) ; les tests ciblés échouent d’une façon qui suggère un impact plus large ; pour une modification Runtime avant PR (suite Runtime hors Stress et contention de charge, une fois) ; ou pour l’ultime vérification avant livraison finale (**une seule fois**, pas à chaque itération). Dire explicitement pourquoi. La suite backend complète non filtrée et les tests lourds restent réservés à la CI.
5. **Frontend Angular** : `ng lint --lint-file-patterns <fichiers>`, `ng test --include <spec>`, et seulement les specs Playwright concernées.
6. **Commandes lourdes** (build, format, test) avec `nice -n 19`, **jamais en parallèle** entre elles : un seul build / format / test à la fois par session.
7. **Rapport final** : indiquer le périmètre vérifié (fichiers, projets, tests) et la justification de chaque élargissement.

**Réservé à la CI par défaut :**

- Seule exception légère en CI sur `dev` : le workflow `backend-dev.yml` (job `backend-dev-fast`) compile avec `-warnaserror` et n'exécute que `ArchitectureTests` et `SqlStatementsPrepareTests` ainsi que le contrôle léger des filtres documentés sur les PR et push vers `dev`.
- Tests média de `Encoding.Runtime.Tests` (`MediaToolsFact`, `MkvToolsFact`) et de `Encoding.Plugins.Tests` (audio, noms de pistes, sous-titres, outils de script) : ignorés en local avec `ENCODING_TEST_SKIP_MEDIA=1`, exécutés par la CI.
- Suite backend complète non filtrée (outils média exigés via `ENCODING_TEST_REQUIRE_MEDIA_TOOLS=1`, collecte de couverture, artefacts `.trx`) : job `backend-runtime-tests` de `.github/workflows/backend.yml`.
- Contention de charge `Category=ContentionLoad` : scénario mixte de `LoadContentionTests`, exécuté par `backend-runtime-tests` sur la PR de promotion ; exclure ce scénario des vérifications locales. Les autres tests de la classe restent accessibles localement.
- Tests de stress `Category=Stress` (`LoadStressTests`, variables `ENCODING_STRESS_EXECUTIONS` / `ENCODING_STRESS_NODES` / `ENCODING_STRESS_SECONDS`) : workflow `.github/workflows/stress.yml` (**déclenchement manuel uniquement**, plus de nightly), exclu des PR par le filtre `Category!=Stress`. **CI uniquement, sans exception locale.**
- Tests multi-processus réels (`WorkerProcessLossTests`, `ENCODING_TEST_MULTIPROCESS=1` : vrai serveur, vrais workers, SIGKILL en plein encodage, plusieurs minutes) : job `backend-multiprocess-tests`.
- Tests cluster Docker (`DockerCentralTests`, `ENCODING_TEST_DOCKER=1` + `ENCODING_TEST_CENTRAL_IMAGE` / `ENCODING_TEST_WORKER_IMAGE` construites) : job `backend-docker-cluster-tests`.
- Mesures de charge et de coordination (`MeasuresTwoServerOwnershipLoad`, `ENCODING_TEST_LARGE_COORDINATION_MEASUREMENTS=1`, 7 000 exécutions en file) et toute mesure de performance / benchmark : job `cluster-two-server-measurements` et workflows dédiés.
- Tests natifs des CLI IA (`AiCliNativeCompatibilityTests`, `ENCODING_TEST_AI_CLI_NATIVE=1`) : job `ai-cli-native`.
- Confinement réel du bac à sable (`ProcessSandboxNativeTests` : Landlock, seccomp, réseau TCP/UDP en bouclage, `/proc`) : job `ai-cli-native` avec `ENCODING_TEST_LANDLOCK=1`, qui rend l'indisponibilité du noyau bloquante. Sans cette variable le test s'exécute quand le poste le permet (léger, aucun accès réseau public) et s'ignore sinon ; les règles pures (`ProcessSandboxRulesTests`) tournent partout.
- E2E lourds, scénarios multi-workers complets et traitements longs : workflows GitHub Actions adaptés.

Les tests conditionnés par les variables `ENCODING_TEST_*` sont ignorés par défaut (xUnit `Skip`) tant que ces variables ne sont pas définies. Les catégories Stress et ContentionLoad nécessitent les filtres locaux indiqués ci-dessus. Ne définissez pas ces variables dans le profil courant. `Category=Stress` et `Category=ContentionLoad` ne doivent pas être lancés localement. Lorsque c’est possible, les commandes locales et la CI utilisent les mêmes filtres ; la CI reste l’autorité pour la couverture complète.

Le workflow `cluster-two-server-measurements` référence `MeasuresTwoServerOwnershipLoad` (7 000 tâches), mais le test correspondant est absent du code présent. Le filtre CI n'a pas été modifié et cette mesure n'est pas comptée comme une validation disponible.
