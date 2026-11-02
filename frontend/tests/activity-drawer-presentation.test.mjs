import assert from 'node:assert/strict';
import test from 'node:test';

function parseSizeDifference(sourceBytes, finalBytes) {
  if (sourceBytes == null || finalBytes == null || sourceBytes <= 0) return null;
  const diff = finalBytes - sourceBytes;
  const percentNum = (diff / sourceBytes) * 100;
  const sign = diff > 0 ? '+' : '';
  return {
    diff,
    percent: `${sign}${percentNum.toFixed(1)} %`,
    reduced: diff < 0,
    expanded: diff > 0,
    neutral: diff === 0,
  };
}

function parseFps(rate) {
  if (typeof rate !== 'string' || !rate) return undefined;
  if (!rate.includes('/')) {
    const num = parseFloat(rate);
    return Number.isFinite(num) && num > 0
      ? `${num.toFixed(num % 1 === 0 ? 0 : 3)} fps`
      : undefined;
  }
  const [numStr, denStr] = rate.split('/');
  const num = parseFloat(numStr);
  const den = parseFloat(denStr);
  if (!Number.isFinite(num) || !Number.isFinite(den) || den === 0) return undefined;
  const fps = num / den;
  return `${fps.toFixed(fps % 1 === 0 ? 0 : 3)} fps`;
}

test('calcule le gain de stockage en pourcentage et signe', () => {
  // Reduction
  const reduced = parseSizeDifference(1000, 750);
  assert.equal(reduced.diff, -250);
  assert.equal(reduced.percent, '-25.0 %');
  assert.equal(reduced.reduced, true);
  assert.equal(reduced.expanded, false);

  // Expansion
  const expanded = parseSizeDifference(1000, 1050);
  assert.equal(expanded.diff, 50);
  assert.equal(expanded.percent, '+5.0 %');
  assert.equal(expanded.reduced, false);
  assert.equal(expanded.expanded, true);

  // Identical
  const same = parseSizeDifference(1000, 1000);
  assert.equal(same.diff, 0);
  assert.equal(same.percent, '0.0 %');
  assert.equal(same.neutral, true);
});

test('gère les tailles nulles ou invalides pour la variation', () => {
  assert.equal(parseSizeDifference(null, 500), null);
  assert.equal(parseSizeDifference(500, null), null);
  assert.equal(parseSizeDifference(0, 500), null);
});

test('convertit les cadences rationnelles en fps décimal ou entier', () => {
  assert.equal(parseFps('24/1'), '24 fps');
  assert.equal(parseFps('24000/1001'), '23.976 fps');
  assert.equal(parseFps('30000/1001'), '29.970 fps');
  assert.equal(parseFps('60/1'), '60 fps');
  assert.equal(parseFps('invalid'), undefined);
  assert.equal(parseFps(''), undefined);
});
