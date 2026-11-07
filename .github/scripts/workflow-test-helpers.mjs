import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

export const readWorkflow = name => readFileSync(new URL(`../workflows/${name}.yml`, import.meta.url), 'utf8');

// Extract the actual indented job/step, never a copy of its implementation.
// YAML syntax and GitHub Actions semantics are checked separately with actionlint.
function block(source, heading, boundary) {
  const lines = source.split('\n');
  const start = lines.findIndex(line => line === heading);
  assert.notEqual(start, -1, `Missing workflow block: ${heading}`);
  let end = start + 1;
  while (end < lines.length && !boundary.test(lines[end])) end++;
  return lines.slice(start, end).join('\n');
}

export const job = (source, name) => block(source, `  ${name}:`, /^  [\w-]+:/);
export const step = (source, name) => block(source, `      - name: ${name}`, /^      - /);

export function value(source, key) {
  const lines = source.split('\n');
  const matches = lines.map((line, index) => ({ match: line.match(new RegExp(`^(\\s*)${key}: (.*)$`)), index }))
    .filter(entry => entry.match);
  assert.equal(matches.length, 1, `Expected exactly one ${key}`);
  const { match, index } = matches[0];
  const [, indentation, scalar] = match;
  if (!['|', '>-'].includes(scalar)) return scalar.replace(/^(['"])(.*)\1$/, '$2');
  const body = [];
  for (const line of lines.slice(index + 1)) {
    if (line.trim() && line.search(/\S/) <= indentation.length) break;
    body.push(line.slice(indentation.length + 2));
  }
  return body.join(scalar === '|' ? '\n' : ' ').trimEnd();
}

// Evaluate the actual &&, || and == expressions, including GitHub's hyphenated job IDs.
export function expression(source, context) {
  const code = source.replace(/^\$\{\{\s*|\s*\}\}$/g, '')
    .replace(/\b(needs|steps)\.([\w-]+)/g, (_, root, name) => `${root}[${JSON.stringify(name)}]`);
  return runInNewContext(code, context, { timeout: 1000 });
}
