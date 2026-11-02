# Référence de conception — Ressources, Fiches et Surfaces Material 3 Expressive

Ce document complète [UI_RULES.md](../../UI_RULES.md) et définit la grammaire visuelle **Material Design 3 Expressive** appliquée aux listes de ressources, aux fiches détaillées (`ResourceSheet`), aux tableaux de bord et aux espaces de travail de l’application.

---

## 1. Philosophie visuelle M3 Expressive

1. **Profondeur tonale sans cages de bordures** :
   - Les ressources et sections se distinguent par leur **surface tonale** (`--surface-container-low`, `--surface-container`, `--surface-container-high`, `--surface-container-highest`) plutôt que par une accumulation de cadres `1px solid`.
   - Dans un groupe de sections (`ResourceSheet`) ou une liste de ressources, utiliser des **conteneurs connectés M3** : coins extérieurs amples (`--radius-xl: 20px` ou `--radius-2xl: 28px`), coins intérieurs doux (`--radius-sm: 8px`) et séparation par un léger interstice (`gap: 3px`–`4px`) laissant apparaître la surface parente.
2. **Contrastes d’échelle et focalisation immédiate** :
   - L’œil doit identifier en moins d’une seconde : **l’identité de la ressource**, **son état opérationnel** (pilule tonale), **sa métrique ou capacité prioritaire** (chiffre expressif ou puce mise en avant) et **l’action principale**.
3. **Mouvement réactif (Spring Motion)** :
   - Le survol, la sélection et l’ouverture utilisent les ressorts M3 (`--motion-spring-fast`, `--motion-spring-standard`) avec une transition subtile de rayon de bordure (*shape morphing*) et de teinte de conteneur.

---

## 2. Structure d’une page de ressource ou de catalogue

### 2.1. En-tête expressif (`PageHeaderComponent`)
- **Titre contextuel** : Nom de l’espace ou de l’objet consulté (ex. nom du nœud local dans *Ce nœud*, nom du cluster dans *Cluster*), mis en valeur avec une typographie expressive (`--font-headline` / `--font-display-sm`).
- **Sous-titre / Métadonnée courte** : Précise le contexte immédiat sans nécessiter de bannière redondante en dessous.
- **Barre d’actions** : Une seule action primaire en bouton plein M3 (`matButton="filled"` en pilule), accompagnée d’actions secondaires tonales ou contourées (`matButton="tonal"` / `matButton="outlined"`).

### 2.2. Synthèse et Métriques héroïques (`MetricCardComponent`, `SummaryTilesComponent`)
- Les cartes de métriques en haut de vue (Vue d’ensemble, Cluster) mettent en scène des **chiffres héroïques** (`--font-display-sm` / `--font-display-md`, `tabular-nums`) sur des conteneurs tonaux doux avec un indicateur visuel de tendance ou de statut.
- Ne jamais afficher de tuile pour une mesure inexistante côté serveur.

---

## 3. Présentation d’une ressource dans une liste (`ResourceCard` & `DataTable layout="resources"`)

Chaque ressource (membre du cluster, bibliothèque média, flow, plugin, profil, script, groupe, utilisateur) occupe une carte horizontale souple pleine largeur :

```text
╭──────────────────────────────────────────────────────────────────────────────╮
│ [☐]  Nom de la ressource   [État tonal] [Badge]        Consulter →    [ ⋮ ]  │
├─────────────────────────┬──────────────────────────┬─────────────────────────┤
│ SECTION / MÉTRIQUE 1    │ SECTION / CAPACITÉ 2     │ SECTION / ACTIVITÉ 3    │
│ Puce principale · Faits │ Détails ou matériel      │ Valeur + Jauge M3       │
╰──────────────────────────────────────────────────────────────────────────────╯
```

- **En-tête de carte** :
  - Case `<mat-checkbox>` si la liste supporte des actions groupées.
  - Titre cliquable ouvrant la fiche ou l’éditeur principal.
  - Badges d’état et de fonction en pilules tonales.
  - Action d’ouverture explicite et menu `⋮` (`MatMenu`) regroupant les actions secondaires (modifier, exporter, dupliquer, supprimer).
- **Sections horizontales adaptatives** :
  - Les colonnes s’adaptent automatiquement au contenu (`grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr))`) dans des sous-conteneurs tonaux (`--surface-container-low`) avec des coins adoucis, sans créer de cellules vides inutiles.

---

## 4. Présentation d’une ressource ouverte (`ResourceSheetComponent`)

Qu’elle soit affichée en pleine page (comme l’onglet **Ce nœud**) ou dans un tiroir de consultation (détail d’un membre du cluster, d’une bibliothèque, d’une ressource partagée, d’un flow, d’un plugin, d’un modèle ou d’un pack), la fiche utilise `ResourceSheetComponent` :

Dans les catalogues de bibliothèques, Ressources, Modèles de flow et Packs de composants, l’ouverture d’un élément garde la liste visible et affiche sa fiche dans `DetailDrawer`. Les onglets qui changent de catalogue continuent d’ouvrir leur route dédiée ; le tiroir est réservé à la fiche de l’élément sélectionné.

1. **Bandeaux d’avis (`notices`)** : Affichés en tête uniquement lorsqu’une intervention ou une anomalie existe (agent injoignable, changement de rôle en attente, nœud en vidange).
2. **En-tête de synthèse** : Titre, sous-titre et statut global.
3. **Pile de sections connectées (*Connected Sections*)** :
   - Chaque section répond à une question cohérente (*État et activité*, *Matériel déclaré*, *Capacités de traitement*, *Accès et stockage*, *Identité*).
   - Les sections s’enchaînent dans un conteneur groupé M3 Expressive (surfaces `--surface-container` séparées par un fin interstice sur fond `--surface-container-low`, coins extérieurs `24px`, coins intérieurs `8px`).
   - Les jauges de charge utilisent une piste épaisse M3 (`height: 8px`–`10px`, `border-radius: 999px`) et passent en motif hachuré (`is-stale`) lorsque la donnée provient du dernier signal d’un nœud hors ligne.
4. **Aperçu visuel intégré** : Pour les ressources de type Flow, Modèle ou Pack de composants, accompagner la fiche d’un aperçu ou d’un résumé clair de sa structure.
5. **Fiche dans un tiroir à onglets** : Le corps du `DetailDrawer` peut être configuré en `bodyLayout="fill"` lorsque le parent fixe sa hauteur. La rangée d’onglets reste en haut du corps ; le panneau actif occupe l’espace restant et porte son propre défilement. Le pied du tiroir reste visible en bas, sans suivre le contenu. `ResourceSheet` accepte `fill` dans ce contexte afin que sa pile remplisse la hauteur disponible tout en pouvant s’étendre si ses sections dépassent cette hauteur. Le corps défilant classique reste le comportement par défaut des autres tiroirs.
