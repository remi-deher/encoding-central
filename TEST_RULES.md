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

## 8. Vérifications locales légères ; charges lourdes réservées à la CI

**Règle absolue : en local, aucun test de charge ni test lourd.** La suite backend complète, les tests de stress, les clusters multi-processus ou multi-conteneurs (Docker), les E2E lourds et les mesures de performance sont exécutés **uniquement par la CI GitHub Actions**. En local, on se limite à des vérifications légères et rapides.

**Léger — autorisé en local :**

- Lint et format : `npm run lint`, `npm run format:flows`, `npm run format:settings` (frontend).
- Compilation / typecheck : `dotnet build <projet.csproj> --no-restore` sur les projets touchés (avec `-warnaserror` et le `.editorconfig` racine ; la solution entière uniquement dans les cas d’élargissement ci-dessous), `npm run build`.
- Tests unitaires ciblés et rapides : `npm test` et `node --test tests/*.test.mjs` (frontend) ; côté backend, des filtres ciblés, par exemple `dotnet test backend/tests/Encoding.Runtime.Tests -c Release --filter "Category!=Stress&FullyQualifiedName!~WorkerProcessLossTests&FullyQualifiedName!~DockerCentralTests"`, ou des classes de tests précises liées au changement.
- E2E léger : `npm run test:e2e` (parcours Playwright smoke, API simulée, sans backend ni média réel).

**Périmètre minimal — économie CPU (backend .NET et frontend Angular) :** vérifier d’abord **uniquement les fichiers modifiés ou créés**. Liste : `git diff --name-only HEAD` plus `git ls-files --others --exclude-standard`.

1. **Format** : jamais `dotnet format` sur `Encoding.sln` pendant le travail courant. Utiliser `dotnet format <projet.csproj> --verify-no-changes --no-restore --include <fichiers modifiés>`.
2. **Build** : seulement les projets touchés, `dotnet build <projet.csproj> --no-restore`, pas la solution.
3. **Tests** : d’abord ceux liés aux fichiers modifiés, `dotnet test <projet.Tests.csproj> --no-build --filter "FullyQualifiedName~<Classe ou namespace>"`.
4. **Élargissement** (projet entier, puis solution) seulement si : un contrat partagé est modifié (interface, DTO, classe de base, configuration commune, `Directory.Build.props`, package) ; les tests ciblés échouent d’une façon qui suggère un impact plus large ; ou pour l’ultime vérification avant commit / livraison finale (**une seule fois**, pas à chaque itération). Dire explicitement pourquoi. L’élargissement reste soumis aux limites « léger / lourd » de cette section : la suite complète et les tests lourds restent réservés à la CI.
5. **Frontend Angular** : `ng lint --lint-file-patterns <fichiers>`, `ng test --include <spec>`, et seulement les specs Playwright concernées.
6. **Commandes lourdes** (build, format, test) avec `nice -n 19`, **jamais en parallèle** entre elles : un seul build / format / test à la fois par session.
7. **Rapport final** : indiquer le périmètre vérifié (fichiers, projets, tests) et la justification de chaque élargissement.

**Lourd — CI uniquement, ne jamais lancer en local :**

- Seule exception légère en CI sur `dev` : le workflow `backend-dev.yml` (job `backend-dev-fast`) compile avec `-warnaserror` et n'exécute que `ArchitectureTests` sur les PR et push vers `dev`.
- Suite backend complète non filtrée (outils média exigés via `ENCODING_TEST_REQUIRE_MEDIA_TOOLS=1`, collecte de couverture, artefacts `.trx`) : job `backend-runtime-tests` de `.github/workflows/backend.yml`.
- Tests de stress `Category=Stress` (`LoadStressTests`, variables `ENCODING_STRESS_EXECUTIONS` / `ENCODING_STRESS_NODES` / `ENCODING_STRESS_SECONDS`) : workflow `.github/workflows/stress.yml` (nightly + manuel), exclu des PR par le filtre `Category!=Stress`.
- Tests multi-processus réels (`WorkerProcessLossTests`, `ENCODING_TEST_MULTIPROCESS=1` : vrai serveur, vrais workers, SIGKILL en plein encodage, plusieurs minutes) : job `backend-multiprocess-tests`.
- Tests cluster Docker (`DockerCentralTests`, `ENCODING_TEST_DOCKER=1` + `ENCODING_TEST_CENTRAL_IMAGE` / `ENCODING_TEST_WORKER_IMAGE` construites) : job `backend-docker-cluster-tests`.
- Mesures de charge et de coordination (`MeasuresTwoServerOwnershipLoad`, `ENCODING_TEST_LARGE_COORDINATION_MEASUREMENTS=1`, 7 000 exécutions en file) et toute mesure de performance / benchmark : job `cluster-two-server-measurements` et workflows dédiés.
- Tests natifs des CLI IA (`AiCliNativeCompatibilityTests`, `ENCODING_TEST_AI_CLI_NATIVE=1`) : job `ai-cli-native`.
- E2E lourds, scénarios multi-workers complets et traitements longs : workflows GitHub Actions adaptés.

Ces tests lourds sont ignorés par défaut (xUnit `Skip`) tant que leurs variables `ENCODING_TEST_*` ne sont pas définies : **ne pas définir ces variables en local**. Lorsque c’est possible, les commandes locales et la CI utilisent les mêmes filtres ; la CI reste l’autorité pour la couverture complète.
