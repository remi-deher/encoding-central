# #30 Évaluation d'un frontend Rust (Leptos, Dioxus) sur la vue Workers, face à Angular

Évaluation mesurée, **pas une migration**. Aucune décision de stack n'est engagée : l'interface Angular,
`AGENTS.md` et `UI_RULES.md` restent en vigueur. Branche `eval/frontend-rust-workers`, sans PR.
Mesures du 2026-11-08 sur la machine de développement (Linux x86_64, 12 cœurs, Chromium headless 156).

## 1. Recommandation

**Ne pas aller**, ni pour l'interface entière ni restreint à la page Workers.

1. **Le gain de légèreté ne vient pas du langage.** À fonctionnalités égales, le prototype Angular + Material
   est **plus léger** que les deux prototypes Rust (144 Kio transférés contre 234 Kio pour Leptos et 316 Kio
   pour Dioxus) et **plus rapide sur réseau lent** (1,7 s contre 2,5 s et 2,9 s). La mémoire au repos est la
   même pour les trois (environ 17 Mio au-dessus d'une page vide). L'écart observé avec l'interface actuelle
   (444 Kio pour `/workers`) vient de ce que l'appli complète charge (shell, navigation, autres flux), pas d'Angular.
2. **Aucun des deux kits ne couvre nos besoins ni Material 3.** Pas de stepper dans les deux, pas de tableau
   dans le kit Dioxus, tableau minimal dans Thaw (ni tri, ni pagination, ni virtualisation). Thaw suit Fluent 2
   (Microsoft) et le kit Dioxus suit shadcn/Radix : changer de framework oblige à abandonner Material 3 Expressive,
   donc à réécrire `UI_RULES.md`.
3. **Risque de maturité fort côté Leptos.** Le framework est actif, mais son kit Thaw est quasi à l'arrêt : 3 commits
   en 2026, aucune version stable pour Leptos 0.8 (seulement `0.5.0-beta` de mai 2025), alors que Leptos 0.9 est
   en RC. Le dialogue Thaw échoue au contrôle clavier (voir §4). Aucun autre kit Leptos n'est maintenu à un niveau comparable.
4. **Restreindre à la page Workers est le pire cas** : deux frameworks, deux systèmes de design, deux chaînes de
   build et de test dans l'image, pour un gain nul en poids.

Si la légèreté du worker reste l'objectif, la piste mesurable est ailleurs : le poids de l'image et du runtime
du worker (#31, .NET et outils média) et l'allègement de la page Workers Angular (444 Kio contre 144 Kio pour
le minimum équivalent : chargement différé du shell et des flux non nécessaires).

Si Rémi souhaite malgré tout un frontend Rust, **Dioxus** est le seul candidat plausible (kit officiel actif,
accessibilité du dialogue correcte). Il faudrait alors une décision explicite et un ADR, avec abandon de Material 3.

## 2. État réel des candidats (vérifié le 2026-11-08)

Sources : API crates.io, API GitHub, code des dépôts clonés.

| | Leptos | Thaw (kit Leptos) | Dioxus | DioxusLabs/components (kit Dioxus) |
|---|---|---|---|---|
| Version stable | 0.8.23 (2026-10-31) | 0.4.8 (2025-08), **pour Leptos 0.7** | 0.7.10 (2026-07-30) | aucune : `dioxus-primitives` 0.0.1 en dépendance **git** seulement |
| Prochaine version | 0.9.0-rc1 (2026-10-31) | 0.5.0-beta (2025-05) pour Leptos 0.8 | 0.8.0-alpha.3 (2026-10-16) | branche principale |
| Activité 2026 | 685 commits, 67 auteurs | **3 commits**, 3 auteurs, dernier le 2026-05-07 | 233 commits, 81 auteurs | 71 commits, 16 auteurs, dernier le 2026-09-08 |
| Licence | MIT | MIT | MIT OR Apache-2.0 | MIT OR Apache-2.0 |
| Modèle de composants | — | crate classique | — | composants **copiés dans le projet** (`dx components add`), à maintenir nous-mêmes |

Autres kits Leptos vérifiés (aucun ne remplace Thaw) : `leptonic` (2024, abandonné), `leptos-shadcn-ui` (2025-09),
`singlestage` (2026-01, environ 2 900 téléchargements récents), `biji-ui`, `leptos-ui`, `leptos_material` (2024).
Je n'ajoute pas de troisième candidat : Leptos ne s'effondre pas, c'est son écosystème de composants qui manque.

## 3. Prototypes

Même périmètre dans les trois : liste des workers (`GET /api/v1/workers`), détail au clic, demandes de
rattachement (`GET /api/v1/workers/enrollment-requests`), action simple « Rattacher un worker » par clé de jumelage
dans un dialogue (`POST /api/v1/workers/enrollment-requests/approve`, messages 404 et erreur repris de l'interface
actuelle), notification de succès. Même contrat d'API, servi par la même API simulée (`mock-api/server.mjs`).

| Prototype | Fichier | Lignes | Composants du kit utilisés |
|---|---|---|---|
| `leptos-workers` | `src/main.rs` | 268 | Table, Badge, Button, Card, Dialog, Field, Input, Toast, MessageBar, Spinner |
| `dioxus-workers` | `src/main.rs` (+ 1 231 lignes de composants copiés) | 276 | Button, Badge, Card, Dialog, Input, Label, Toast ; **tableau HTML écrit à la main** |
| `angular-workers` (référence équivalente) | `src/main.ts` | 127 | mat-table, mat-dialog, mat-form-field, matInput, mat-chip, mat-card, MatSnackBar |

Non couvert par les prototypes (identique pour les trois) : flux SSE `/api/v1/live`, routage, authentification,
thème sombre, assistant de rattachement par code de vérification.

## 4. Mesures

### 4.1 Taille du paquet (mesuré)

Tout ce que le navigateur télécharge pour afficher la page (HTML, JS, wasm, CSS). gzip -9 et brotli -q 11.

| Cible | Brut | gzip | brotli |
|---|---:|---:|---:|
| Angular minimal (Material) | 577 Kio | **146 Kio** | **124 Kio** |
| Leptos + Thaw | 643 Kio | 239 Kio | 192 Kio |
| Dioxus + kit, après retrait des infos de débogage | 859 Kio | 322 Kio | 247 Kio |
| Dioxus + kit, sortie `dx build --release` brute | 1 256 Kio | 417 Kio | 327 Kio |
| Angular complet, route `/workers` (transfert mesuré) | — | 444 Kio | — |

`dx` passe `--debuginfo` à wasm-opt même avec `debug = false` : il faut une étape de plus pour obtenir la taille
optimale (`measure/strip-dioxus.sh`).

### 4.2 Chargement et repos (mesuré, `measure/bench.mjs`)

Temps entre la navigation et l'affichage visible de « worker-1 » (médiane), premier rendu (FCP), octets transférés
hors API. Repos : 30 s après 5 s de stabilisation, CPU du processus de rendu et mémoire PSS.

**Local, sans bridage (7 essais)**

| Cible | Liste affichée | FCP | Transfert | CPU au repos | PSS rendu | Tas JS |
|---|---:|---:|---:|---:|---:|---:|
| Page vide (plancher) | 80 ms | 44 ms | 0 | 80 ms/min | 57 Mio | 1,3 Mio |
| Angular complet `/workers` | 863 ms | 416 ms | 444 Kio | 500 ms/min | 101 Mio | 8,7 Mio |
| Angular minimal | 334 ms | 228 ms | 144 Kio | 160 ms/min | 74 Mio | 3,8 Mio |
| Leptos | **202 ms** | 152 ms | 234 Kio | 40 ms/min | 75 Mio | 1,7 Mio |
| Dioxus | 211 ms | 172 ms | 316 Kio | 60 ms/min | 74 Mio | 1,8 Mio |

**4G lente simulée (150 ms de latence, 1,6 Mbit/s, processeur ralenti 4×, 3 essais)**

| Cible | Liste affichée | FCP |
|---|---:|---:|
| Page vide | 377 ms | 248 ms |
| Angular complet `/workers` | 5 559 ms | 2 368 ms |
| Angular minimal | **1 718 ms** | **1 408 ms** |
| Leptos | 2 521 ms | 1 992 ms |
| Dioxus | 2 942 ms | 2 412 ms |

Lecture : en local, le wasm gagne environ 130 ms sur Angular minimal ; dès que le réseau compte, le poids
l'emporte et Angular minimal est le plus rapide. La mémoire au repos est la même pour les trois prototypes
(PSS 74 à 75 Mio, soit 17 à 18 Mio au-dessus de la page vide) ; le tas JS plus petit des prototypes Rust est
compensé par la mémoire linéaire wasm. Le CPU au repos des trois prototypes est au niveau du bruit (page vide :
80 ms/min). Le CPU au repos sous bridage 4× n'est pas exploitable (l'émulation gonfle le temps CPU) et n'est pas retenu.

### 4.3 Accessibilité (mesuré, `measure/a11y.mjs`, axe-core WCAG 2.2 A/AA et parcours clavier)

| Contrôle | Angular minimal | Leptos + Thaw | Dioxus + kit |
|---|---|---|---|
| axe, page | 0 violation | 0 violation | `html-has-lang` (modèle `index.html` par défaut de dx, corrigeable) |
| axe, dialogue ouvert | 0 violation | 0 violation | `color-contrast` (thème du kit), `html-has-lang` |
| Dialogue nommé (`aria-labelledby`) | oui | **non** | oui |
| Focus placé dans le dialogue à l'ouverture | oui | **non** | oui |
| Focus piégé dans le dialogue (Tab) | oui | **non** | oui |
| Échap ferme | oui | oui | oui |
| Focus rendu au bouton déclencheur | oui | **non** | oui |
| Champ relié à son libellé | oui | oui | oui |
| Succès annoncé dans une région `aria-live` | oui (LiveAnnouncer du CDK) | **non** (Toast sans `aria-live`) | oui |

Les manques de Thaw viennent de la bibliothèque (vérifié dans le code de 0.5.0-beta et de la branche principale :
`DialogSurface` n'a que `role="dialog" aria-modal="true"`, aucune gestion du focus ; le toast n'a pas de région
`aria-live`). Non mesuré : lecteur d'écran réel (NVDA, VoiceOver), zoom 200 %, affichage à 320 px.

### 4.4 Couverture des composants nécessaires (vérifié dans le code des kits)

| Besoin | Angular Material/CDK | Thaw 0.5.0-beta | DioxusLabs/components |
|---|---|---|---|
| Tableau (tri, pagination) | oui (`mat-table`, `MatSort`, `MatPaginator`) | tableau simple, colonnes redimensionnables, **ni tri ni pagination** | **absent** |
| Dialogue | oui | oui (accessibilité incomplète) | oui (+ alert dialog, sheet) |
| Stepper | oui (`mat-stepper`, `CdkStepper`) | **absent** | **absent** |
| Formulaires (champ, validation, erreurs) | oui (formulaires réactifs typés, `mat-error`) | Field, Input, Select, Combobox, Checkbox, Radio, DatePicker | Form, Input, Label, Select, Combobox, Checkbox, Radio |
| Liste virtualisée | oui (`cdk-virtual-scroll`) | **absent** | oui (`virtual_list`) |
| Infobulle | oui | oui | oui |
| Système de design Material 3 | oui | non (Fluent 2) | non (style shadcn) |

### 4.5 i18n (vérifié, non prototypé)

L'interface actuelle n'utilise pas d'i18n (textes français en dur, aucun `@angular/localize`). Côté Rust :
`leptos_i18n` 0.6.2 (2026-04) ou `leptos-fluent` 0.3.1 ; `dioxus-i18n` 0.6.0 (2026-10, Fluent). Thaw fournit une
locale `FrFR` pour ses propres composants. Aucun écart bloquant, mais rien d'équivalent à l'extraction intégrée d'Angular.

### 4.6 Réutilisation des tests E2E (mesuré en partie)

- L'infrastructure est réutilisable telle quelle : Playwright, API simulée par `page.route`, `@axe-core/playwright`.
  Les scripts `measure/bench.mjs` et `measure/a11y.mjs` tournent sans modification sur les trois prototypes,
  parce qu'ils interrogent par rôle et libellé.
- Les 16 spécifications existantes (98 tests) visent l'appli complète : 536 lignes interrogent par rôle, libellé ou texte
  (portables si le balisage et les libellés sont conservés) et 186 lignes par sélecteur Angular ou CSS
  (`app-…`, classes) qui seraient à réécrire. Aucune spécification ne couvre aujourd'hui la page Workers.

### 4.7 Effort de développement (mesuré sur ce prototype, estimé au-delà)

| | Angular minimal | Leptos | Dioxus |
|---|---|---|---|
| Itérations de compilation pour aboutir | 1 | 4 (conflit Thaw 0.4 → Leptos 0.7, puis 2 erreurs de propriété dans `view!`) | 1, mais chaîne `dx` obligatoire (CSS modules du kit) |
| Construction complète à froid | 29 s (appli complète) | environ 6 min (release) | environ 2 min 40 s (cargo) + 3 min (`dx`, wasm-opt) |
| Reconstruction après une ligne modifiée | 6 à 9 s (production) | 21 s (debug), 16 à 62 s (release) | 6 s (debug), 16 s (release) |
| Outillage à installer | Node déjà présent | rustup + cible wasm32, wasm-bindgen, binaryen | rustup + cible wasm32, `dx` (télécharge aussi binaryen dans `~/.local/share/.dx`) |

Estimation (non mesurée) d'une migration complète : l'interface fait environ 73 000 lignes dans 353 fichiers hors tests,
avec un design system Material 3 à reconstruire et des composants absents (stepper, tableau riche) à écrire.
C'est un chantier de plusieurs mois, sans gain de poids démontré.

## 5. Mesuré et non mesuré

**Mesuré** : versions, activité et licences ; taille des paquets ; temps de chargement local et 4G lente ;
CPU, PSS et tas JS au repos (local) ; axe-core et parcours clavier du dialogue ; couverture des kits (code source) ;
portabilité des sélecteurs E2E (comptage) ; temps de compilation des prototypes.

**Non mesuré** : lecteur d'écran réel ; zoom 200 % et 320 px ; thème sombre ; SSE et volumes réels (centaines de
workers) ; consommation sur le matériel du worker (NAS exclu de cette évaluation) ; poids de l'image du worker
(attendu de #31) ; effort d'une migration complète (estimation seulement) ; navigateurs autres que Chromium.

## 6. Limites

- Une seule machine, Chromium headless ; le bridage réseau et CPU est une émulation.
- Les prototypes couvrent une petite partie de la page Workers réelle ; la référence équitable est Angular minimal,
  pas l'appli complète.
- La mesure PSS inclut le socle du processus de rendu ; seule la différence avec la page vide est significative.
- Les tailles Rust dépendent des réglages (`opt-level = "z"`, LTO, wasm-opt `-Oz`) ; ce sont déjà les plus favorables.

## 7. Reproduire

Voir `README.md`.
