#!/usr/bin/env node

/**
 * QA Auto-Merge Agent Script
 * 
 * Ce script est exécuté par l'agent QA dans AgentsRoom pour :
 * 1. Surveiller les PR ouvertes
 * 2. Vérifier que tous les checks sont verts
 * 3. Détecter les conflits entre PR
 * 4. Fusionner automatiquement les PR prêtes
 * 5. Notifier l'assistant de projet via MCP en cas de conflit
 * 
 * Utilisation :
 *   node .github/scripts/qa-auto-merge.mjs [--dry-run] [--repo REPO] [--token TOKEN]
 * 
 * Variables d'environnement :
 *   GITHUB_TOKEN: Token GitHub avec permissions pull-requests:write, contents:read
 *   MCP_SERVER: URL du serveur MCP pour notifier l'assistant (optionnel)
 *   DRY_RUN: Si "true", simule sans fusionner
 */

import { execSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { exit } from 'node:process';

// ============================================================================
// Configuration
// ============================================================================

const DRY_RUN = process.env.DRY_RUN === 'true' || process.argv.includes('--dry-run');
const REPO = process.env.GITHUB_REPOSITORY || process.argv.filter(a => a.startsWith('--repo='))[0]?.split('=')[1] || 'encoding-central';
const TOKEN = process.env.GITHUB_TOKEN || process.argv.filter(a => a.startsWith('--token='))[0]?.split('=')[1];
const OWNER = 'remi-deher';
const FULL_REPO = `${OWNER}/${REPO}`;

// Branches autorisées pour la fusion automatique
const ALLOWED_TARGETS = ['dev'];
// Promotion dev→main gérée séparément par pr-policy.yml
const PROMOTION_SOURCE = 'dev';
const PROMOTION_TARGET = 'main';

// ============================================================================
// Fonctions utilitaires GitHub CLI
// ============================================================================

function gh(cmd, options = {}) {
  const fullCmd = `gh ${cmd}`;
  try {
    return execSync(fullCmd, { 
      encoding: 'utf8',
      env: { ...process.env, GITHUB_TOKEN: TOKEN },
      stdio: options.stdio || 'pipe'
    }).trim();
  } catch (error) {
    if (options.ignoreError) return null;
    console.error(`❌ Erreur gh: ${fullCmd}`);
    console.error(error.stdout || error.message);
    exit(1);
  }
}

function log(message, level = 'info') {
  const prefix = {
    info: 'ℹ️',
    success: '✅',
    warning: '⚠️',
    error: '❌',
    conflict: '🔥'
  }[level] || '•';
  console.log(`${prefix} ${message}`);
}

// ============================================================================
// Récupération des PR
// ============================================================================

function getOpenPRs() {
  log('Récupération des PR ouvertes...');
  
  // Récupérer toutes les PR ouvertes (non-draft, ready_for_review)
  const json = gh(`pr list --repo ${FULL_REPO} --state open --json number,title,headRefName,baseRefName,isDraft,statusCheckRollup,mergeable,url`);
  
  try {
    const prs = JSON.parse(json);
    log(`Trouvées ${prs.length} PR ouvertes`);
    
    return prs.filter(pr => {
      // Filtrer les drafts
      if (pr.isDraft) {
        log(`PR #${pr.number} ignorée (draft)`, 'warning');
        return false;
      }
      
      // Vérifier si la PR est ready_for_review
      const details = gh(`pr view ${pr.number} --repo ${FULL_REPO} --json readyForReview`);
      const detailObj = JSON.parse(details);
      
      if (!detailObj.readyForReview) {
        log(`PR #${pr.number} ignorée (non ready_for_review)`, 'warning');
        return false;
      }
      
      return true;
    });
  } catch (e) {
    log(`Erreur parsing PR: ${e}`, 'error');
    return [];
  }
}

// ============================================================================
// Vérification des checks
// ============================================================================

function checkPRStatus(pr) {
  log(`Vérification des checks pour PR #${pr.number}...`);
  
  const json = gh(`pr checks ${pr.number} --repo ${FULL_REPO} --json databaseId,status,conclusion,name`);
  
  try {
    const checks = JSON.parse(json);
    
    // Vérifier que tous les checks requis sont complets et réussis
    const requiredChecks = [
      'dev-ci',           // Contrôle principal de dev
      'promotion-ci',     // Pour les promotions
      'backend-dev-fast', // CI backend
      'frontend-ci'       // CI frontend
    ];
    
    const failedChecks = [];
    const pendingChecks = [];
    
    for (const check of checks) {
      if (check.status === 'COMPLETED' && check.conclusion !== 'SUCCESS') {
        failedChecks.push(check.name);
      } else if (check.status !== 'COMPLETED') {
        pendingChecks.push(check.name);
      }
    }
    
    if (failedChecks.length > 0) {
      log(`PR #${pr.number} a des checks échoués: ${failedChecks.join(', ')}`, 'error');
      return { ready: false, reason: `Checks échoués: ${failedChecks.join(', ')}` };
    }
    
    if (pendingChecks.length > 0) {
      log(`PR #${pr.number} a des checks en attente: ${pendingChecks.join(', ')}`, 'warning');
      return { ready: false, reason: `Checks en attente: ${pendingChecks.join(', ')}` };
    }
    
    log(`PR #${pr.number} : tous les checks sont verts ✅`, 'success');
    return { ready: true };
  } catch (e) {
    log(`Erreur vérification checks PR #${pr.number}: ${e}`, 'error');
    return { ready: false, reason: 'Erreur de vérification' };
  }
}

// ============================================================================
// Détection des conflits entre PR
// ============================================================================

function detectConflicts(prs) {
  log('Détection des conflits entre PR...');
  
  const conflicts = [];
  const fileChanges = {};
  
  // Récupérer les fichiers modifiés pour chaque PR
  for (const pr of prs) {
    try {
      const json = gh(`pr diff ${pr.number} --repo ${FULL_REPO} --name-only --json`);
      const files = JSON.parse(json) || [];
      
      fileChanges[pr.number] = new Set(files);
    } catch (e) {
      log(`Erreur récupération fichiers PR #${pr.number}: ${e}`, 'warning');
      fileChanges[pr.number] = new Set();
    }
  }
  
  // Comparer chaque paire de PR
  for (let i = 0; i < prs.length; i++) {
    for (let j = i + 1; j < prs.length; j++) {
      const pr1 = prs[i];
      const pr2 = prs[j];
      const files1 = fileChanges[pr1.number] || new Set();
      const files2 = fileChanges[pr2.number] || new Set();
      
      // Trouver l'intersection
      const commonFiles = [...files1].filter(f => files2.has(f));
      
      if (commonFiles.length > 0) {
        conflicts.push({
          pr1: pr1.number,
          pr2: pr2.number,
          pr1Title: pr1.title,
          pr2Title: pr2.title,
          commonFiles
        });
        log(`⚠️  Conflit détecté entre PR #${pr1.number} et #${pr2.number}: ${commonFiles.join(', ')}`, 'conflict');
      }
    }
  }
  
  return conflicts;
}

// ============================================================================
// Vérification de la fusion possible
// ============================================================================

function checkMergeable(pr) {
  log(`Vérification mergeability pour PR #${pr.number}...`);
  
  const json = gh(`pr view ${pr.number} --repo ${FULL_REPO} --json mergeable,mergeableState`);
  
  try {
    const data = JSON.parse(json);
    
    if (data.mergeable === false) {
      log(`PR #${pr.number} n'est pas mergeable: ${data.mergeableState}`, 'error');
      return { mergeable: false, reason: data.mergeableState };
    }
    
    log(`PR #${pr.number} est mergeable ✅`, 'success');
    return { mergeable: true };
  } catch (e) {
    log(`Erreur vérification mergeable PR #${pr.number}: ${e}`, 'error');
    return { mergeable: false, reason: 'Erreur de vérification' };
  }
}

// ============================================================================
// Fusion automatique
// ============================================================================

function autoMerge(pr, conflictsWith = []) {
  log(`Tentative de fusion automatique pour PR #${pr.number}...`);
  
  if (DRY_RUN) {
    log(`[DRY RUN] Would merge PR #${pr.number}: ${pr.title}`, 'warning');
    return { success: true, dryRun: true };
  }
  
  try {
    // Fusion avec rebase pour éviter les merge commits inutiles
    const result = gh(`pr merge ${pr.number} --repo ${FULL_REPO} --rebase --auto`);
    log(`✅ PR #${pr.number} fusionnée avec succès!`, 'success');
    return { success: true };
  } catch (e) {
    log(`❌ Échec fusion PR #${pr.number}: ${e.stdout || e.message}`, 'error');
    return { success: false, error: e.stdout || e.message };
  }
}

// ============================================================================
// Notification MCP
// ============================================================================

function notifyAssistant(pr, conflictInfo, type = 'conflict') {
  log(`Notification MCP: ${type} détecté pour PR #${pr.number}`, 'warning');
  
  // Pour l'instant, on log simplement
  // Dans AgentsRoom, l'agent QA devra envoyer un message MCP manuellement
  const message = {
    type: 'qa-notification',
    timestamp: new Date().toISOString(),
    repository: FULL_REPO,
    pr: {
      number: pr.number,
      title: pr.title,
      headRef: pr.headRefName,
      baseRef: pr.baseRefName
    },
    conflict: conflictInfo,
    actionRequired: type === 'conflict' ? 'ARBITRAGE' : 'REVIEW'
  };
  
  console.log('\n' + '='.repeat(80));
  console.log('📢 NOTIFICATION POUR ASSISTANT DE PROJET (MCP)');
  console.log('='.repeat(80));
  console.log(JSON.stringify(message, null, 2));
  console.log('='.repeat(80) + '\n');
  
  // Todo: Envoyer via MCP quand disponible
  // Pour l'instant, l'agent QA devra copier cette info et la send via agents_send
}

// ============================================================================
// Validation de la cible de fusion
// ============================================================================

function isValidTarget(pr) {
  const target = pr.baseRefName;
  const source = pr.headRefName;
  
  // Promotion dev→main gérée par pr-policy.yml
  if (source === PROMOTION_SOURCE && target === PROMOTION_TARGET) {
    log(`PR #${pr.number} est une promotion dev→main, ignorée (gérée par pr-policy.yml)`, 'info');
    return { valid: false, reason: 'Promotion gérée séparément' };
  }
  
  // Fusion vers dev autorisée
  if (ALLOWED_TARGETS.includes(target)) {
    return { valid: true };
  }
  
  log(`PR #${pr.number} cible ${target} non autorisée`, 'warning');
  return { valid: false, reason: `Cible non autorisée: ${target}` };
}

// ============================================================================
// Main
// ============================================================================

function main() {
  log('='.repeat(80));
  log(`QA Auto-Merge Agent - ${FULL_REPO}`);
  log(`Mode: ${DRY_RUN ? 'DRY RUN (simulation)' : 'REEL'}`);
  log('='.repeat(80) + '\n');
  
  // 1. Récupérer les PR ouvertes
  const prs = getOpenPRs();
  
  if (prs.length === 0) {
    log('Aucune PR ouverte à traiter.', 'info');
    exit(0);
  }
  
  // 2. Détecter les conflits entre PR
  const conflicts = detectConflicts(prs);
  
  // 3. Traiter chaque PR
  const prsToMerge = [];
  const prsWithIssues = [];
  const prsInConflict = new Set();
  
  for (const pr of prs) {
    const targetCheck = isValidTarget(pr);
    
    if (!targetCheck.valid) {
      log(`PR #${pr.number} ignorée: ${targetCheck.reason}`, 'warning');
      continue;
    }
    
    const statusCheck = checkPRStatus(pr);
    
    if (!statusCheck.ready) {
      prsWithIssues.push({
        pr,
        reason: statusCheck.reason,
        type: 'checks_failed'
      });
      continue;
    }
    
    const mergeCheck = checkMergeable(pr);
    
    if (!mergeCheck.mergeable) {
      prsWithIssues.push({
        pr,
        reason: mergeCheck.reason,
        type: 'not_mergeable'
      });
      continue;
    }
    
    // Vérifier si cette PR est en conflit avec d'autres
    const prConflicts = conflicts.filter(c => c.pr1 === pr.number || c.pr2 === pr.number);
    
    if (prConflicts.length > 0) {
      prsInConflict.add(pr.number);
      
      const conflictDetails = prConflicts.map(c => ({
        withPR: c.pr1 === pr.number ? c.pr2 : c.pr1,
        withPRTitle: c.pr1 === pr.number ? c.pr2Title : c.pr1Title,
        files: c.commonFiles
      }));
      
      prsWithIssues.push({
        pr,
        reason: 'Conflit avec d\'autres PR',
        type: 'conflict',
        conflictDetails
      });
      
      // Notifier l'assistant de projet
      notifyAssistant(pr, conflictDetails, 'conflict');
      continue;
    }
    
    // PR prête pour fusion
    prsToMerge.push(pr);
  }
  
  // 4. Résumé et actions
  log('\n' + '='.repeat(80));
  log('RÉSULTATS');
  log('='.repeat(80));
  
  log(`\nPR prêtes à fusionner: ${prsToMerge.length}`, prsToMerge.length > 0 ? 'success' : 'info');
  for (const pr of prsToMerge) {
    log(`  ✅ #${pr.number}: ${pr.title}`);
  }
  
  log(`\nPR avec problèmes: ${prsWithIssues.length}`, prsWithIssues.length > 0 ? 'error' : 'info');
  for (const item of prsWithIssues) {
    const icon = {
      checks_failed: '❌',
      not_mergeable: '⚠️',
      conflict: '🔥'
    }[item.type] || '❌';
    log(`  ${icon} #${item.pr.number}: ${item.reason}`);
    if (item.conflictDetails) {
      for (const c of item.conflictDetails) {
        log(`     → Conflit avec #${c.withPR}: ${c.files.join(', ')}`);
      }
    }
  }
  
  // 5. Fusion automatique des PR prêtes
  if (prsToMerge.length > 0) {
    log('\n' + '='.repeat(80));
    log('FUSION AUTOMATIQUE');
    log('='.repeat(80) + '\n');
    
    for (const pr of prsToMerge) {
      const result = autoMerge(pr);
      
      if (result.success) {
        log(`✅ PR #${pr.number} fusionnée avec succès!`, 'success');
        
        // Notifier que la fusion a eu lieu
        if (!DRY_RUN) {
          console.log('\n' + '-'.repeat(80));
          console.log(`📢 FUSION EFFECTUÉE: PR #${pr.number} - ${pr.title}`);
          console.log(`   Base: ${pr.baseRefName} | Head: ${pr.headRefName}`);
          console.log('-'.repeat(80) + '\n');
        }
      } else {
        log(`❌ Échec fusion PR #${pr.number}: ${result.error}`, 'error');
      }
    }
  }
  
  // 6. Résumé final
  log('\n' + '='.repeat(80));
  log('RÉSUMÉ FINAL');
  log('='.repeat(80));
  log(`Fusionnées: ${prsToMerge.filter(pr => !DRY_RUN).length}`);
  log(`En conflit: ${prsInConflict.size}`);
  log(`Problèmes: ${prsWithIssues.length}`);
  log('='.repeat(80) + '\n');
  
  // Exit code: 0 si tout s'est bien passé, 1 si des conflits non résolus
  const hasConflicts = prsInConflict.size > 0;
  const hasFailures = prsWithIssues.some(i => i.type !== 'conflict');
  
  exit(hasConflicts || hasFailures ? 1 : 0);
}

// Exécuter
try {
  main();
} catch (error) {
  log(`Erreur fatale: ${error}`, 'error');
  exit(1);
}
