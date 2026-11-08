# Évaluation #30 : frontend Rust (Leptos, Dioxus) face à Angular, vue Workers

Dossier d'évaluation isolé. **Ne fait pas partie du produit** : ne pas l'intégrer, ne pas ouvrir de PR vers `test`.
Le rapport est dans [REPORT.md](REPORT.md).

## Contenu

- `leptos-workers/` : prototype Leptos 0.8.23 + Thaw 0.5.0-beta.
- `dioxus-workers/` : prototype Dioxus 0.7.10 + DioxusLabs/components (composants copiés par `dx components add`).
- `angular-workers/` : même périmètre en Angular + Material, référence équitable (utilise `../../frontend/node_modules`).
- `blank/` : page vide, plancher des mesures.
- `mock-api/server.mjs` : serveur statique gzip + API `/api/v1/workers*` simulée, commun aux cibles.
- `measure/` : empaquetage, tailles, banc de chargement et de repos, contrôles d'accessibilité.
- `results/` : sorties JSON des mesures du rapport.

## Outillage (isolé, hors système)

```sh
export RUSTUP_HOME=<dossier>/rustup CARGO_HOME=<dossier>/cargo PATH=<dossier>/cargo/bin:$PATH
sh rustup-init.sh -y --no-modify-path --profile minimal --target wasm32-unknown-unknown
# wasm-bindgen 0.2.130 (version du Cargo.lock), binaryen version_133, dx 0.7.10 : binaires des releases GitHub,
# sommes de contrôle vérifiées.
(cd ../../frontend && npm ci) && PLAYWRIGHT_BROWSERS_PATH=<dossier>/pw npx playwright install chromium
```

## Construire

```sh
# Leptos
(cd leptos-workers && nice -n 19 cargo build --release --target wasm32-unknown-unknown)
WASM_BINDGEN=… WASM_OPT=… measure/package.sh leptos-workers leptos-workers leptos-workers/app.css
# Dioxus
(cd dioxus-workers && nice -n 19 dx build --release --platform web)
WASM_OPT=… measure/strip-dioxus.sh dioxus-workers
# Angular minimal et complet
(cd angular-workers && node node_modules/@angular/cli/bin/ng.js build)
(cd ../../frontend && npx ng build --configuration production)
```

## Mesurer

```sh
measure/sizes.sh leptos-workers/dist     # tailles brut / gzip / brotli
node mock-api/server.mjs <dist> <port>   # un serveur par cible
PROFILE=local RUNS=7 node measure/bench.mjs angular-complet=http://127.0.0.1:8801/workers?tab=cluster \
  angular-minimal=http://127.0.0.1:8805/ leptos=http://127.0.0.1:8802/ dioxus=http://127.0.0.1:8803/ \
  page-vide=http://127.0.0.1:8806/
PROFILE=slow4g RUNS=3 IDLE_MS=5000 node measure/bench.mjs …
node measure/a11y.mjs angular-minimal=… leptos=… dioxus=…
```
