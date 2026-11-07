import { test } from 'node:test';
import assert from 'node:assert/strict';

// Tests pour la matrice de policy PR
test('PR vers test depuis ce dépôt : autorisée', () => {
  const env = {
    BASE_REF: 'test',
    HEAD_REF: 'feature/xyz',
    HEAD_REPO: 'remi-deher/encoding-central',
    GITHUB_REPOSITORY: 'remi-deher/encoding-central'
  };
  // Simulation de la logique : test accepte toute branche SAUF main (source = ce dépôt)
  const isAuthorized = env.BASE_REF === 'test' 
    && env.HEAD_REF !== 'main' 
    && env.HEAD_REPO === env.GITHUB_REPOSITORY;
  assert.equal(isAuthorized, true);
});

test('PR vers test depuis main : refusée', () => {
  const env = {
    BASE_REF: 'test',
    HEAD_REF: 'main',
    HEAD_REPO: 'remi-deher/encoding-central',
    GITHUB_REPOSITORY: 'remi-deher/encoding-central'
  };
  const isAuthorized = env.BASE_REF === 'test' 
    && env.HEAD_REF !== 'main' 
    && env.HEAD_REPO === env.GITHUB_REPOSITORY;
  assert.equal(isAuthorized, false);
});

test('PR vers dev depuis test (même dépôt) : autorisée', () => {
  const env = {
    BASE_REF: 'dev',
    HEAD_REF: 'test',
    HEAD_REPO: 'remi-deher/encoding-central',
    GITHUB_REPOSITORY: 'remi-deher/encoding-central'
  };
  const isAuthorized = env.BASE_REF === 'dev' 
    && env.HEAD_REF === 'test' 
    && env.HEAD_REPO === env.GITHUB_REPOSITORY;
  assert.equal(isAuthorized, true);
});

test('PR vers dev depuis test (fork) : refusée', () => {
  const env = {
    BASE_REF: 'dev',
    HEAD_REF: 'test',
    HEAD_REPO: 'fork/encoding-central',
    GITHUB_REPOSITORY: 'remi-deher/encoding-central'
  };
  const isAuthorized = env.BASE_REF === 'dev' 
    && env.HEAD_REF === 'test' 
    && env.HEAD_REPO === env.GITHUB_REPOSITORY;
  assert.equal(isAuthorized, false);
});

test('PR vers dev depuis feature : refusée', () => {
  const env = {
    BASE_REF: 'dev',
    HEAD_REF: 'feature/xyz',
    HEAD_REPO: 'remi-deher/encoding-central',
    GITHUB_REPOSITORY: 'remi-deher/encoding-central'
  };
  const isAuthorized = env.BASE_REF === 'dev' 
    && env.HEAD_REF === 'test' 
    && env.HEAD_REPO === env.GITHUB_REPOSITORY;
  assert.equal(isAuthorized, false);
});

test('PR vers main depuis dev : autorisée', () => {
  const env = {
    BASE_REF: 'main',
    HEAD_REF: 'dev',
    HEAD_REPO: 'remi-deher/encoding-central',
    GITHUB_REPOSITORY: 'remi-deher/encoding-central'
  };
  const isAuthorized = env.BASE_REF === 'main' && env.HEAD_REF === 'dev';
  assert.equal(isAuthorized, true);
});

test('PR vers main depuis test : refusée', () => {
  const env = {
    BASE_REF: 'main',
    HEAD_REF: 'test',
    HEAD_REPO: 'remi-deher/encoding-central',
    GITHUB_REPOSITORY: 'remi-deher/encoding-central'
  };
  const isAuthorized = env.BASE_REF === 'main' && env.HEAD_REF === 'dev';
  assert.equal(isAuthorized, false);
});
