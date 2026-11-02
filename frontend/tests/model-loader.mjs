import { readFile } from 'node:fs/promises';
import ts from 'typescript';

const urls = new Map();
async function modelUrl(name) {
  if (urls.has(name)) return urls.get(name);
  const pending = (async () => {
    const source = await readFile(
      new URL(`../src/app/shared/models/${name}.ts`, import.meta.url),
      'utf8',
    );
    let { outputText } = ts.transpileModule(source, {
      compilerOptions: { module: ts.ModuleKind.ES2022, target: ts.ScriptTarget.ES2022 },
    });
    for (const match of [...outputText.matchAll(/from ['"]\.\/([a-z-]+)['"]/g)]) {
      outputText = outputText.replace(match[0], `from '${await modelUrl(match[1])}'`);
    }
    return `data:text/javascript;base64,${Buffer.from(outputText).toString('base64')}`;
  })();
  urls.set(name, pending);
  return pending;
}
export async function loadModel(name) {
  return import(await modelUrl(name));
}
