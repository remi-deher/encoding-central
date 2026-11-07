# Agent QA - Guide d'Opération

## 🎯 Rôle

L'**Agent QA** est responsable de :
1. ✅ **Fusionner automatiquement** les PR quand tous les checks sont verts
2. ✅ **Détecter les conflits** entre PR ouvertes
3. ✅ **Assurer la cohérence** des modifications
4. ✅ **Notifier l'assistant de projet** via MCP en cas de conflit nécessitant arbitrage

## 📋 Prérequis

### Accès GitHub
- Un **token GitHub** avec les permissions :
  - `repo` (accès complet)
  - `pull-requests: write` (pour fusionner)
  - `contents: read` (pour lire les fichiers)
  - `checks: read` (pour vérifier les status)
- Le token doit avoir accès à **tous les dépôts** :
  - `remi-deher/encoding-central`
  - `remi-deher/encoding-store`
  - `remi-deher/linux-plugins`
  - `remi-deher/ryoku`

### Configuration GitHub CLI
```bash
# Installer GitHub CLI
gh auth login
# Ou avec token
echo "VOTRE_TOKEN" | gh auth login --with-token
```

### Configuration AgentsRoom
- L'agent QA doit avoir accès au **serveur MCP** du projet
- L'assistant de projet doit être identifié dans AgentsRoom

---

## 🚀 Procédures

### 1. Surveillance manuelle des PR

Exécuter le script de QA auto-merge :

```bash
# Dans encoding-central
cd /home/remi/GitHub/encoding-central
node .github/scripts/qa-auto-merge.mjs

# Avec dry-run (simulation)
DRY_RUN=true node .github/scripts/qa-auto-merge.mjs

# Pour un autre dépôt
GITHUB_REPOSITORY=remi-deher/encoding-store node .github/scripts/qa-auto-merge.mjs
```

### 2. Fusion automatique via workflow GitHub

Déclencher manuellement le workflow :

```bash
# Déclencher sur encoding-central
gh workflow run qa-auto-merge.yml --repo remi-deher/encoding-central

# Avec dry-run
gh workflow run qa-auto-merge.yml --repo remi-deher/encoding-central --field dry_run=true

# Pour encoding-store
gh workflow run qa-auto-merge.yml --repo remi-deher/encoding-store
```

### 3. Surveillance continue

Le workflow peut être configuré pour s'exécuter automatiquement :

1. Dans `.github/workflows/qa-auto-merge.yml`, décommentez la section `schedule` :
```yaml
schedule:
  - cron: '*/30 * * * *'  # Toutes les 30 minutes
```

2. Ou déclenchez après chaque succès de CI en ajoutant :
```yaml
workflow_run:
  workflows:
    - 'Backend dev'
    - 'Frontend'
  types:
    - completed
```

---

## 🔍 Détection des conflits

Le script détecte automatiquement les conflits entre PR en comparant les fichiers modifiés.

### Exemple de conflit détecté :
```
🔥 Conflit détecté entre PR #123 et #456: 
   - src/backend/Encoding.cs
   - tests/Encoding.Tests.cs
```

### Actions en cas de conflit :
1. **Ne pas fusionner** les PR en conflit
2. **Notifier l'assistant de projet** via MCP
3. **Attendre l'arbitrage** avant de fusionner

---

## 📢 Notification MCP à l'assistant de projet

Quand un conflit est détecté, le script affiche une notification formatée :

```json
{
  "type": "qa-notification",
  "timestamp": "2024-01-15T10:30:00.000Z",
  "repository": "remi-deher/encoding-central",
  "pr": {
    "number": 123,
    "title": "fix: correction du bug de encoding",
    "headRef": "fix/encoding-bug",
    "baseRef": "dev"
  },
  "conflict": [
    {
      "withPR": 456,
      "withPRTitle": "feat: nouvelle fonctionnalité",
      "files": ["src/backend/Encoding.cs"]
    }
  ],
  "actionRequired": "ARBITRAGE"
}
```

### Comment envoyer la notification :

L'agent QA doit **copier cette notification** et l'envoyer via MCP :

```bash
# Dans AgentsRoom, utiliser la commande MCP pour envoyer à l'assistant
a Providence RPC call or use the MCP tools
```

Ou via l'interface AgentsRoom :
1. Sélectionner l'assistant de projet dans la liste des agents
2. Envoyer un message avec les informations du conflit
3. Marquer comme "ARBITRAGE REQUIS"

---

## ✅ Critères de fusion automatique

Une PR sera **fusionnée automatiquement** si :

- [ ] ✅ **Statut** : Ouverte (non-draft) + ready_for_review
- [ ] ✅ **Cible** : `dev` (ou promotion `dev→main` gérée séparément)
- [ ] ✅ **Tous les checks** sont verts :
  - `dev-ci`
  - `promotion-ci` (pour les promotions)
  - `backend-dev-fast`
  - `frontend-ci`
- [ ] ✅ **Aucun conflit** avec d'autres PR ouvertes
- [ ] ✅ **Mergeable** : GitHub considère la PR comme mergeable

---

## ❌ Cas de non-fusion

Une PR **ne sera pas fusionnée** si :

- ❌ **Draft** : La PR est en mode draft
- ❌ **Non ready_for_review** : La PR n'est pas marquée comme prête
- ❌ **Checks échoués** : Un ou plusieurs checks sont rouges
- ❌ **Checks en attente** : Certains checks ne sont pas terminés
- ❌ **Conflit de merge** : GitHub détecte un conflit
- ❌ **Conflit avec autre PR** : Modifie les mêmes fichiers qu'une autre PR ouverte
- ❌ **Cible non autorisée** : Fusion vers une branche non autorisée (sauf dev→main)

---

## 📊 Rapports et Logging

### Logs du script
Le script affiche un rapport détaillé :

```
ℹ️ QA Auto-Merge Agent - remi-deher/encoding-central
ℹ️ Mode: REEL
================================================================================

PR prêtes à fusionner: 2
  ✅ #123: fix: correction du bug de encoding
  ✅ #124: chore: mise à jour des dépendances

PR avec problèmes: 1
  🔥 #125: Conflit avec d'autres PR
     → Conflit avec #123: src/backend/Encoding.cs

================================================================================
FUSION AUTOMATIQUE
================================================================================

✅ PR #123 fusionnée avec succès!
✅ PR #124 fusionnée avec succès!

================================================================================
RÉSUMÉ FINAL
================================================================================
Fusionnées: 2
En conflit: 1
Problèmes: 0
================================================================================
```

### Historique GitHub
Toutes les fusions sont enregistrées dans l'historique Git avec :
- Message de commit standard
- Auteur : GitHub CLI (configurable)
- Méthode : Rebase (pour éviter les merge commits inutiles)

---

## 🛠️ Configuration par dépôt

### encoding-central
- ✅ Workflow : `.github/workflows/qa-auto-merge.yml`
- ✅ Script : `.github/scripts/qa-auto-merge.mjs`
- ✅ Branches autorisées : `dev`
- ✅ Promotion : `dev→main` (gérée par pr-policy.yml)

### encoding-store
- À configurer (même structure)
- Branches : `main` (à vérifier)

### linux-plugins
- À configurer
- Branches : `main` (à vérifier)

### ryoku
- À configurer
- Branches : `main` (à vérifier)

---

## 🔄 Maintenance

### Mettre à jour le script
```bash
# Tester les modifications
node .github/scripts/qa-auto-merge.mjs --dry-run

# Valider les changements
git add .github/scripts/qa-auto-merge.mjs
git commit -m "chore(qa): mise à jour du script auto-merge"
git push
```

### Vérifier les permissions
```bash
# Tester l'accès
gh pr list --repo remi-deher/encoding-central
gh pr checks 123 --repo remi-deher/encoding-central
```

---

## 🚨 Procédures d'urgence

### Arrêter les fusions automatiques
1. **Désactiver le workflow** : Commenter la section `schedule` dans qa-auto-merge.yml
2. **Mode dry-run permanent** : Définir `DRY_RUN: true` dans les variables d'environnement
3. **Révoker le token** : Supprimer le token GitHub utilisé

### Récupérer après un conflit
Si une fusion a causé un conflit :
1. **Annuler la fusion** (si nécessaire) :
   ```bash
   git revert <commit-hash>
   git push
   ```
2. **Résoudre le conflit** manuellement
3. **Relancer le QA** :
   ```bash
   node .github/scripts/qa-auto-merge.mjs
   ```

---

## 📞 Contacts

- **Assistant de projet** : À notifier via MCP en cas de conflit
- **Développeurs** : Contacter via AgentsRoom ou GitHub
- **Problèmes techniques** : Ouvrir une issue dans le dépôt concerné

---

## 📝 Historique des changements

| Date | Version | Changements |
|------|---------|-------------|
| 2024-11-07 | 1.0.0 | Création initiale |
