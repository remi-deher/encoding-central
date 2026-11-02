import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
const root = resolve('dist/ai-widget/browser');
let html = await readFile(resolve(root, 'index.html'), 'utf8');
const css = await readFile(resolve(root, 'styles.css'), 'utf8');
const js = await readFile(resolve(root, 'main.js'), 'utf8');
html = html.replace(
  /<link rel="stylesheet" href="styles.css"[^>]*>/,
  () => `<style>${css.replace(/<\/style/gi, '<\\/style')}</style>`,
);
html = html.replace(
  /<script src="main.js"[^>]*><\/script>/,
  () => `<script type="module">${js.replace(/<\/script/gi, '<\\/script')}</script>`,
);
html = html.replace('<base href="/">', '<base href="./">');
await writeFile(resolve(root, 'maquette-widget-ia-v2.html'), html);
console.log(resolve(root, 'maquette-widget-ia-v2.html'));
