import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { URL } from 'node:url';

// Test de la policy YAML réelle
const policyYaml = readFileSync(new URL('../workflows/pr-policy.yml', import.meta.url), 'utf8');

test('PR policy YAML contient les bonnes règles', () => {
  // Vérifier que test accepte toute branche sauf main depuis ce dépôt
  assert.match(policyYaml, /if.*BASE_REF.*test.*HEAD_REF.*main.*HEAD_REPO.*GITHUB_REPOSITORY/);
  
  // Vérifier que dev accepte test depuis ce dépôt
  assert.match(policyYaml, /if.*BASE_REF.*dev.*HEAD_REF.*test.*HEAD_REPO.*GITHUB_REPOSITORY/);
  
  // Vérifier que main accepte dev
  assert.match(policyYaml, /if.*BASE_REF.*main.*HEAD_REF.*dev/);
});

test('PR policy YAML contient les bonnes actions', () => {
  // Vérifier qu'il y a une action de fermeture pour les PR non autorisées
  assert.match(policyYaml, /gh pr close/);
  
  // Vérifier qu'il y a une action d'auto-merge pour dev→main
  assert.match(policyYaml, /gh pr merge.*--auto/);
});
