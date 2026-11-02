# Stabilisation des blocs de flow

La reprise de l'étape 5 est terminée sur la branche `codex/flow-editor-compact-branches`. Les PR précédentes #189 et #190 ont été intégrées à `dev` avant cette stabilisation.

Les cinq lots sont décrits dans [fiche ui](../architecture/domains/ui.md). La stabilisation corrige la composition audio/normalisation, la conformité de fréquence audio, la conversion ASS sans style, les résumés de règles, l'affichage des ressources et la visibilité inline. L'attribution exige désormais le jeu de polices sur le worker sélectionné.

Les scénarios de ressources couvrent extraction réelle, stockage durable, reprise après relais, consommation sur un autre worker, référence absente sur une branche, référence future et empreinte invalide. Aucun média utilisateur n'est utilisé.

Le test de restauration du cluster attend désormais l'échec prévu de la tentative dont le worker ne renouvelle plus le bail. Il ne suppose plus que toute la maintenance se termine avant les 60 secondes du bail. Les garanties du runtime ne changent pas.

Les commandes de vérification restent :

```powershell
C:/Users/remi2/.dotnet/dotnet test backend/tests/Encoding.Runtime.Tests/Encoding.Runtime.Tests.csproj -m:1
cd frontend
npm run lint
npm run build
npm test
npm run test:e2e -- e2e/flow-editor.spec.ts --workers=2
```

Les scénarios média nécessitent FFmpeg/ffprobe et MKVToolNix, inclus dans l'image de tests. Sous Windows, ajouter leur dossier au PATH du processus. Les tests Angular peuvent limiter leurs workers avec `VITEST_MAX_WORKERS=2`.

Les comportements utilisateur et les limites externes sont décrits dans [le guide de flow](fileflows-v3-flow.md). Landlock et les intégrations externes restent à vérifier sous Linux/CI ou sur leurs environnements dédiés ; aucune publication ni opération sur une bibliothèque personnelle n'est nécessaire à ces tests.

Vérification du 21 octobre 2026 : 267 tests backend, 93 tests de modèles, 140 tests Angular et 52 parcours E2E ordinateur/mobile passent. Les derniers ajustements de conteneur et de références sont couverts par une relance ciblée de 16 tests média/ressources. Lint et build réussissent ; les avertissements de budget initial (~2,39 ko) et de styles du tableau (604 octets) restent présents.
