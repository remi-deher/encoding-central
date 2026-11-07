# 🎯 Agent QA - Configuration Globale

## 📋 Sommaire

Ce document décrit la configuration **globale** de l'agent QA qui gère automatiquement la fusion des PR sur **tous vos projets** :
- ✅ `encoding-central`
- ✅ `encoding-store`
- ✅ `linux-plugins`
- ✅ `ryoku`

---

## 🚀 Architecture

### Composants déployés

Pour chaque projet, les fichiers suivants ont été créés :

```
.github/
├── scripts/
│   └── qa-auto-merge.mjs      # Script principal de QA et fusion
└── workflows/
    └── qa-auto-merge.yml      # Workflow GitHub pour exécution
```

### Fonctionnement

1. **L'agent QA** (dans AgentsRoom) exécute le script `qa-auto-merge.mjs`
2. **Le script** :
   - Liste toutes les PR ouvertes et prêtes (ready_for_review, non-draft)
   - Vérifie que tous les checks GitHub sont **verts**
   - Détecte les **conflits** entre PR (fichiers modifiés en commun)
   - Fusionne automatiquement les PR **sans conflit** et **tout vert**
   - **Notifie l'assistant de projet via MCP** en cas de conflit

3. **L'assistant de projet** (via MCP) :
   - Reçoit les notifications de conflit
   - **Arbitre** les conflits
   - Prend les **mesures nécessaires** (résolution, priorisation, etc.)

---

## 🔧 Configuration par Projet

### encoding-central
- **Branches autorisées** : `dev` (les promotions `dev→main` sont gérées par `pr-policy.yml`)
- **Checks requis** : `dev-ci`, `promotion-ci`, `backend-dev-fast`, `frontend-ci`
- **Fichiers** :
  - `.github/scripts/qa-auto-merge.mjs`
  - `.github/workflows/qa-auto-merge.yml`
  - `.github/AGENT_QA.md` (documentation locale)

### encoding-store
- **Branches autorisées** : `main`, `dev`
- **Checks requis** : Tous les checks du workflow CI
- **Fichiers** :
  - `.github/scripts/qa-auto-merge.mjs`
  - `.github/workflows/qa-auto-merge.yml`

### linux-plugins
- **Branches autorisées** : `main`
- **Checks requis** : `mirror`, `validate`
- **Fichiers** :
  - `.github/scripts/qa-auto-merge.mjs`
  - `.github/workflows/qa-auto-merge.yml`

### ryoku
- **Branches autorisées** : `main`
- **Checks requis** : Selon la configuration existante
- **Fichiers** :
  - `.github/scripts/qa-auto-merge.mjs`
  - `.github/workflows/qa-auto-merge.yml`

---

## ⚙️ Configuration Requise

### 1. Token GitHub pour l'Agent QA

L'agent QA a besoin d'un **token GitHub** avec les permissions suivantes :

```yaml
Permissions:
  - repo: full control (ou au minimum)
    - contents: write
    - pull-requests: write
    - checks: read
    - metadata: read
```

#### Créer un token :
1. Aller sur [GitHub → Settings → Developer Settings → Personal Access Tokens](https://github.com/settings/tokens)
2. Créer un **Classic Token**
3. Sélectionner les permissions :
   - ✅ `repo` (accès complet)
4. **Ne pas expirer** (ou définir une longue durée)
5. **Sauvegarder le token** dans un endroit sécurisé

#### Configurer le token dans AgentsRoom :
L'agent QA doit avoir accès à ce token via :
- Variable d'environnement `GITHUB_TOKEN`
- Ou fichier de configuration sécurisé

### 2. Authentification GitHub CLI

Dans AgentsRoom, l'agent QA doit exécuter :

```bash
# Authentifier avec le token
echo "VOTRE_TOKEN_GITHUB" | gh auth login --with-token

# Vérifier l'authentification
gh auth status
```

### 3. Configuration MCP

L'agent QA doit être connecté au **serveur MCP** du projet pour envoyer des notifications à l'assistant.

Dans AgentsRoom :
1. Vérifier que le MCP server est **connecté**
2. Identifier l'**assistant de projet** (par son ID ou nom)
3. Tester la communication MCP

---

## 📖 Procédures d'Opération

### Pour l'Agent QA

#### 1. Surveiller tous les projets

Exécuter le script sur chaque projet :

```bash
# encoding-central
cd /home/remi/GitHub/encoding-central
node .github/scripts/qa-auto-merge.mjs

# encoding-store
cd /home/remi/GitHub/encoding-store
node .github/scripts/qa-auto-merge.mjs

# linux-plugins
cd /home/remi/GitHub/linux-plugins
node .github/scripts/qa-auto-merge.mjs

# ryoku
cd /home/remi/GitHub/ryoku
node .github/scripts/qa-auto-merge.mjs
```

#### 2. Mode simulation (dry-run)

Toujours tester en mode simulation avant de fusionner réellement :

```bash
# Pour un projet
DRY_RUN=true node .github/scripts/qa-auto-merge.mjs

# Pour tous les projets
for repo in encoding-central encoding-store linux-plugins ryoku; do
  cd /home/remi/GitHub/$repo
  DRY_RUN=true node .github/scripts/qa-auto-merge.mjs
  cd -
done
```

#### 3. Surveillance automatique

Déclencher les workflows GitHub :

```bash
# Déclencher manuellement sur encoding-central
gh workflow run qa-auto-merge.yml --repo remi-deher/encoding-central

# Avec dry-run
gh workflow run qa-auto-merge.yml --repo remi-deher/encoding-central --field dry_run=true
```

#### 4. Script global (optionnel)

Créer un script global pour surveiller tous les projets :

```bash
#!/bin/bash
# ~/bin/qa-monitor.sh

REPOS=("encoding-central" "encoding-store" "linux-plugins" "ryoku")
DRY_RUN=${1:-true}  # true par défaut pour la sécurité

for repo in "${REPOS[@]}"; do
  echo "=========================================="
  echo "Traitement: $repo"
  echo "=========================================="
  cd /home/remi/GitHub/$repo
  DRY_RUN=$DRY_RUN node .github/scripts/qa-auto-merge.mjs
  cd -
done
```

Utilisation :
```bash
# Simulation
./qa-monitor.sh true

# Vrai mode (attention!)
./qa-monitor.sh false
```

---

### Pour l'Assistant de Projet

#### Recevoir les notifications MCP

Quand l'agent QA détecte un conflit, il affiche une notification formatée :

```json
{
  "type": "qa-notification",
  "timestamp": "2024-11-07T10:30:00.000Z",
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

#### Actions à entreprendre :

1. **Identifier les PR en conflit** (n°123 et n°456)
2. **Examiner les fichiers en conflit** (`src/backend/Encoding.cs`)
3. **Contacter les auteurs** des PR pour coordination
4. **Décider de la priorité** :
   - Quelle PR fusionner en premier ?
   - Faut-il attendre que l'une des PR soit mise à jour ?
   - Faut-il une résolution manuelle des conflits ?
5. **Communiquer la décision** à l'agent QA

#### Résolution des conflits :

**Option 1 : Fusionner une PR en priorité**
```bash
# L'assistant demande à l'agent QA de fusionner la PR #456 en premier
gh pr merge 456 --repo remi-deher/encoding-central --rebase

# Puis relancer le QA
cd /home/remi/GitHub/encoding-central
node .github/scripts/qa-auto-merge.mjs
```

**Option 2 : Demander une mise à jour**
```bash
# Demander à l'auteur de la PR #123 de rebaser sur dev
gh pr comment 123 --repo remi-deher/encoding-central --body "@author Veuillez rebaser votre PR pour résoudre le conflit avec #456"
```

**Option 3 : Résolution manuelle**
```bash
# Si les conflits sont complexes, créer une branche de résolution
git checkout -b resolve-conflict-123-456
git merge fix/encoding-bug
# Résoudre les conflits manuellement
git add .
git commit -m "Resolve conflict between #123 and #456"
git push origin resolve-conflict-123-456
# Puis fusionner cette branche
```

---

## 📊 Critères de Fusion Automatique

Une PR sera **fusionnée automatiquement** si **TOUTES** ces conditions sont remplies :

| Critère | Valeur requise | Vérification |
|---------|---------------|--------------|
| Statut | Ouverte | ✅ |
| Draft | Non | ✅ |
| Ready for Review | Oui | ✅ |
| Branche cible | Autorisée (`dev`, `main`) | ✅ |
| Tous les checks | ✅ Verts | ✅ |
| Mergeable | Oui (pas de conflit GitHub) | ✅ |
| Conflits avec autres PR | Aucun | ✅ |

---

## 🚨 Gestion des Erreurs

### Problèmes courants et solutions

| Problème | Cause | Solution |
|----------|-------|----------|
| `❌ Erreur gh: authentication failed` | Token GitHub invalide ou expiré | Renouveler le token et réauthentifier |
| `❌ Checks en attente` | CI en cours d'exécution | Attendre la fin des checks |
| `❌ Checks échoués` | Tests ou validation échoués | Corriger les problèmes et relancer |
| `🔥 Conflit détecté` | Plusieurs PR modifient les mêmes fichiers | Arbitrage nécessaire |
| `⚠️  Non mergeable` | Conflit de merge GitHub | Résoudre manuellement ou rebaser |
| `❌ Permission denied` | Permissions insuffisantes | Vérifier les permissions du token |

### Mode récupération

Si une fusion automatique échoue :

1. **Vérifier les logs** du workflow ou du script
2. **Exécuter en dry-run** pour identifier le problème
3. **Fusionner manuellement** si nécessaire
4. **Corriger la configuration** si c'est un problème systémique

---

## 🔄 Maintenance

### Mettre à jour le script

Les scripts sont identiques pour tous les projets. Pour les mettre à jour :

```bash
# Modifier le script dans encoding-central (référence)
cd /home/remi/GitHub/encoding-central
vim .github/scripts/qa-auto-merge.mjs

# Tester
gh workflow run qa-auto-merge.yml --repo remi-deher/encoding-central --field dry_run=true

# Copier vers les autres projets
cp .github/scripts/qa-auto-merge.mjs /home/remi/GitHub/encoding-store/.github/scripts/
cp .github/scripts/qa-auto-merge.mjs /home/remi/GitHub/linux-plugins/.github/scripts/
cp .github/scripts/qa-auto-merge.mjs /home/remi/GitHub/ryoku/.github/scripts/

# Valider les changements dans chaque dépôt
cd /home/remi/GitHub/encoding-central
git add .github/scripts/qa-auto-merge.mjs
git commit -m "chore(qa): mise à jour du script auto-merge"
git push

# Répéter pour chaque dépôt...
```

### Ajouter un nouveau projet

Pour configurer un nouveau projet :

```bash
# Créer la structure
mkdir -p /path/au/projet/.github/scripts
mkdir -p /path/au/projet/.github/workflows

# Copier les fichiers
cp /home/remi/GitHub/encoding-central/.github/scripts/qa-auto-merge.mjs /path/au/projet/.github/scripts/
cp /home/remi/GitHub/encoding-central/.github/workflows/qa-auto-merge.yml /path/au/projet/.github/workflows/

# Adapter la configuration dans qa-auto-merge.yml
# - Vérifier les workflows déclencheurs
# - Adapter les branches autorisées dans le script

# Valider
git add .github/
git commit -m "chore: ajouter configuration QA auto-merge"
git push
```

---

## 📝 Historique

| Date | Version | Auteur | Changements |
|------|---------|--------|-------------|
| 2024-11-07 | 1.0.0 | Agent QA | Configuration initiale pour les 4 projets |

---

## 📞 Support

- **Problèmes techniques** : Vérifier les logs, contacter l'administrateur système
- **Questions sur l'arbitrage** : Contacter l'assistant de projet
- **Améliorations** : Ouvrir une issue dans `encoding-central`

---

## 🔐 Sécurité

### Bonnes pratiques

1. **Ne jamais commiter le token GitHub** dans le dépôt
2. **Utiliser des secrets GitHub** pour les workflows
3. **Limiter les permissions** du token au strict nécessaire
4. **Rotater régulièrement** les tokens (tous les 90 jours recommandé)
5. **Audit des fusions** : Vérifier régulièrement les fusions effectuées

### En cas de compromise

Si le token est compromis :

1. **Révoquer immédiatement** le token sur GitHub
2. **Changer les permissions** des workflows si nécessaire
3. **Créer un nouveau token** avec de nouvelles permissions
4. **Mettre à jour la configuration** dans AgentsRoom
5. **Auditer les actions récentes** pour détecter les abus

---

## ✅ Checklist de déploiement

- [x] Scripts créés pour encoding-central
- [x] Workflows créés pour encoding-central
- [x] Scripts créés pour encoding-store
- [x] Workflows créés pour encoding-store
- [x] Scripts créés pour linux-plugins
- [x] Workflows créés pour linux-plugins
- [x] Scripts créés pour ryoku
- [x] Workflows créés pour ryoku
- [ ] Token GitHub configuré pour l'agent QA
- [ ] Authentification GitHub CLI testée
- [ ] Connexion MCP vérifiée
- [ ] Test en dry-run effectué sur tous les projets
- [ ] Fusion réelle testée sur une PR de test
- [ ] Documentation lue et comprise par l'agent QA et l'assistant
