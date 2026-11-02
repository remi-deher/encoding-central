import test from 'node:test';
import assert from 'node:assert/strict';
import { loadModel } from './model-loader.mjs';
const {
  summarizeSystemLogs,
  mergeSystemLogs,
  formatSystemLog,
  serializeSystemLogs,
  systemLogsCsv,
} = await loadModel('system-log-presentation');
const entry = {
  id: 'one',
  timestamp: '2026-10-19T10:00:00+02:00',
  level: 'Warning',
  category: 'Encoding.Worker',
  message: 'Original untranslated message',
  nodeId: 'first',
  nodeName: 'salon',
  eventId: 17,
};
test('summary groups exact repeats and keeps their original message, dates and IDs', () => {
  const result = summarizeSystemLogs([
    { ...entry, id: 'two', timestamp: '2026-10-19T10:01:00+02:00' },
    entry,
  ]);
  assert.equal(result.length, 1);
  assert.equal(result[0].entry.message, entry.message);
  assert.deepEqual(
    result[0].entries.map((e) => e.id),
    ['one', 'two'],
  );
  assert.equal(result[0].firstSeen, entry.timestamp);
  assert.equal(result[0].lastSeen, '2026-10-19T10:01:00+02:00');
});
test('different levels, services, nodes, event IDs and exceptions never collapse', () => {
  const variants = [
    entry,
    { ...entry, level: 'Error' },
    { ...entry, category: 'Other' },
    { ...entry, nodeId: 'second' },
    { ...entry, eventId: 18 },
    { ...entry, exception: 'Stack trace' },
  ];
  assert.equal(summarizeSystemLogs(variants).length, variants.length);
  assert.equal(summarizeSystemLogs(variants)[0].entry.level, 'Error');
});
test('merge deduplicates stream/history replay, sorts absolute times and bounds memory', () => {
  const later = { ...entry, id: 'two', timestamp: '2026-10-19T08:00:01Z' };
  assert.deepEqual(
    mergeSystemLogs([later, entry, later], 2).map((e) => e.id),
    ['one', 'two'],
  );
  assert.deepEqual(
    mergeSystemLogs([later, entry], 1).map((e) => e.id),
    ['two'],
  );
});
test('console serialization preserves multiline messages and exceptions verbatim', () => {
  const error = {
    ...entry,
    level: 'Error',
    message: 'first\nsecond <script>',
    exception: 'Exception\n   at Worker()',
  };
  const text = `[${entry.timestamp}] [ERROR] [Encoding.Worker] [@salon] first\nsecond <script>\nException\n   at Worker()`;
  assert.equal(formatSystemLog(error), text);
  assert.equal(serializeSystemLogs([error, error]), `${text}\n${text}`);
});
test('CSV escapes quotes, Unicode, newlines and spreadsheet expressions', () => {
  const text = systemLogsCsv([{ ...entry, message: '=formula("é")\nline' }]);
  assert.ok(text.includes('"\'=formula(""é"")\nline"'));
  assert.equal(systemLogsCsv([]).split('\r\n').length, 1);
});
