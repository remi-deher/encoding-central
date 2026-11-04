# Règles de tests et d’intégration continue

Ces règles sont obligatoires pour les changements du projet. Les tests doivent protéger les comportements métier et les garanties du central, tout en restant rapides à exécuter pendant le développement.

## 1. CI GitHub Actions

- Mettre en place GitHub Actions pour exécuter les vérifications du projet sur les changements proposés et poussés.
- La CI doit au minimum installer les dépendances, vérifier le format et le lint, compiler ou vérifier les types, puis exécuter les tests adaptés au changement.
- Les vérifications requises doivent être visibles et réussir avant qu’un changement puisse être intégré.
- Les contrôles locaux autorisés et ceux de GitHub utilisent les mêmes configurations lorsque c’est possible. Le profil local restrictif de la section 8 prime : les compilations et les tests s'exécutent uniquement en CI.
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

- Prévoir un parcours E2E déterministe pour vérifier le chemin critique de l’application, exécuté exclusivement en CI GitHub Actions.
- Réserver les E2E lourds, les scénarios multi-workers complets et les traitements longs aux workflows GitHub Actions adaptés (voir section 8 pour la liste complète des charges lourdes réservées à la CI).
- Garder les E2E indépendants des services personnels, des secrets locaux et des médias non versionnés.
- En cas d’échec, fournir des journaux et des artefacts de diagnostic suffisants pour reproduire le problème.

## 5. Code mort et qualité du code

- Ajouter des vérifications automatisées qui détectent le code mort ou inutilisé selon les outils retenus par le projet.
- Exécuter le lint sans compilation/typecheck localement sur les fichiers modifiés et dans GitHub Actions avec la même configuration. Un script nommé « lint » qui lance `tsc` est réservé à la CI.
- Vérifier le formatage de façon ciblée en local ; réserver la compilation et les types statiques à la CI.
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

**Aucun test .NET en local**, ni test frontend, unitaire, d'intégration, E2E ou de scénario, même ciblé. Par défaut, aucune compilation, aucun typecheck, aucun serveur de développement qui compile ni build Docker sur le poste ; seule l’exception de recette Docker ci-dessous autorise les compilations dans le build approuvé. Cette règle s'applique à tous les agents, au lead et aux vérifications automatiques Friday, y compris sous `nice`, dans Docker ou sur une copie dans `/tmp`. Écrire et corriger les tests sans les exécuter localement.

**Vérifications locales autorisées :** lecture du code, `git diff --check`, vérification de syntaxe sans compilation, format en mode vérification et lint sans typecheck sur les seuls fichiers modifiés. Lancer un seul contrôle à la fois avec `timeout 15s nice -n 19 ...`. Si le contrôle dépasse cette limite, le reporter en CI sans le relancer localement. Dans ce dépôt, `frontend/package.json` définit `lint` comme `tsc --noEmit` : `npm run lint` est donc interdit en local malgré son nom. Ne pas lancer `dotnet format`, même ciblé.

**Commandes réservées à la CI :** `npm test`, `ng test`, Vitest/Jest, Playwright/Cypress, les tests Node/Python, `ng build`, `tsc` (même `--noEmit`), `dotnet build/test/run/publish/restore`, les builds et scénarios Docker, les benchmarks et les traitements média. Aucun contournement par un alias, un autre gestionnaire de paquets, un sous-agent ou une autre équipe.

**Délégation :** le lead regroupe les besoins de validation pour éviter plusieurs suites identiques. Après publication autorisée de la branche, utiliser les contrôles de PR existants ou le déclenchement manuel des workflows `backend.yml` et `frontend.yml` sur cette branche. Ces déclenchements manuels ne seront disponibles qu'après intégration de leur configuration dans la branche par défaut GitHub. Ils utilisent `ubuntu-latest`, jamais un runner auto-hébergé sur ce poste. Attendre les résultats du SHA exact (`gh pr checks`, `gh run view`), transmettre les échecs au développeur et ne jamais annoncer une CI verte sans preuve. Les changements non poussés ne sont pas couverts par une ancienne exécution CI. Ne pas pousser le travail d'un autre agent ni fusionner sans autorisation.

**Friday :** ne pas mettre de compilation ou de test dans le champ `check` des tâches. Si un contrôle local automatique est nécessaire, utiliser uniquement `timeout 15s nice -n 19 git diff --check`. La QA prépare les scénarios et analyse les résultats CI ; elle ne démarre pas l'application localement.

Dans les rapports, distinguer « contrôle statique local réussi », « non exécuté localement — validation CI attendue » et « validation CI réussie sur le SHA … ».

### Exception : recette Docker demandée par le propriétaire

À la fin d’une tâche, le lead vérifie les résultats des tests unitaires pertinents et des contrôles requis sur le SHA exact. Un build ou les seuls tests d’architecture ne suffisent pas. Si ces tests ne sont pas verts, il fait corriger ou compléter la CI et ne propose pas encore la recette.

Une fois les tests verts, il propose au propriétaire de construire les images Docker et de démarrer les Compose pour une recette manuelle. Il attend un accord explicite pour cette livraison : le silence n’autorise rien. Cet accord permet les compilations nécessaires dans la construction Docker, une seule construction à la fois, puis le démarrage de conteneurs avec données et volumes de test isolés et ressources bornées. Les suites automatisées locales, benchmarks et charges média lourdes restent interdits.

Après démarrage, le lead fournit les accès et vérifications suggérées et attend le retour du propriétaire avant toute fusion. Si un défaut est signalé, il fait corriger et valider le nouveau SHA en CI. Une réponse autre qu’une validation ou un refus explicite de recette ne vaut pas accord de fusion.

Après validation de la recette, ou refus explicite de la tester, le lead poursuit le circuit normal : fusion de la PR de tâche vers `dev` seulement si ses contrôles requis sont verts, attente de la CI verte sur le commit exact de `dev`, puis ouverture ou actualisation d’une PR de promotion `dev` → `main`. La CI complète de promotion reste obligatoire. La fusion vers `main` et le déploiement nécessitent une autorisation explicite distincte. Ne pas inclure des changements étrangers à la livraison.

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
