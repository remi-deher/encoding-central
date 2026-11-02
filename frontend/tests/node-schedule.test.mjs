import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';

const {
  SLOT_COUNT,
  SLOTS_PER_DAY,
  allSlots,
  cellState,
  cellsPerDay,
  isOpenAt,
  isValidSlots,
  paintCells,
  paintDay,
} = await loadModel('node-schedule');

test('un calendrier complet contient 672 créneaux valides', () => {
  assert.equal(SLOT_COUNT, 672);
  assert.ok(isValidSlots(allSlots(true)));
  assert.ok(!isValidSlots('101'));
  assert.ok(!isValidSlots('2'.repeat(SLOT_COUNT)));
});

test('peint une plage d’heures sur un jour sans toucher aux autres', () => {
  const slots = paintCells(allSlots(true), 1, 22, 23, 60, false);
  assert.equal(slots.length, SLOT_COUNT);
  assert.equal(cellState(slots, 1, 21, 60), 'on');
  assert.equal(cellState(slots, 1, 22, 60), 'off');
  assert.equal(cellState(slots, 1, 23, 60), 'off');
  assert.equal(cellState(slots, 0, 23, 60), 'on');
  assert.equal(cellState(slots, 2, 22, 60), 'on');
});

test('accepte une plage inversée et borne les indices au jour', () => {
  const reversed = paintCells(allSlots(true), 3, 5, 2, 60, false);
  assert.deepEqual(
    [1, 2, 5, 6].map((cell) => cellState(reversed, 3, cell, 60)),
    ['on', 'off', 'off', 'on'],
  );
  const bounded = paintCells(allSlots(false), 6, 20, 99, 60, true);
  assert.equal(bounded.length, SLOT_COUNT);
  assert.equal(cellState(bounded, 6, 23, 60), 'on');
});

test('affiche une cellule partielle quand ses quarts d’heure diffèrent', () => {
  const slots = paintCells(allSlots(true), 0, 1, 1, 15, false);
  assert.equal(cellState(slots, 0, 0, 60), 'partial');
  assert.equal(cellState(slots, 0, 1, 15), 'off');
  assert.equal(cellState(slots, 0, 0, 30), 'partial');
  assert.equal(cellsPerDay(15), 96);
  assert.equal(cellsPerDay(60), 24);
});

test('peint un jour entier', () => {
  const slots = paintDay(allSlots(true), 5, false);
  assert.ok(
    slots
      .slice(5 * SLOTS_PER_DAY, 6 * SLOTS_PER_DAY)
      .split('')
      .every((slot) => slot === '0'),
  );
  assert.equal(slots.slice(0, 5 * SLOTS_PER_DAY), '1'.repeat(5 * SLOTS_PER_DAY));
});

test('un calendrier désactivé est toujours ouvert', () => {
  const closed = { enabled: true, timeZone: 'UTC', slots: allSlots(false) };
  assert.ok(!isOpenAt(closed, 2, 600));
  assert.ok(isOpenAt({ ...closed, enabled: false }, 2, 600));
});
