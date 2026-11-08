// Contrôles d'accessibilité (#30) : axe-core (WCAG 2.2 A/AA) sur la page puis dialogue ouvert, et parcours
// clavier du rattachement (ouverture au clavier, focus dans le dialogue, piège de focus, Échap, retour du focus,
// approbation au clavier avec la bonne clé). Usage : node a11y.mjs <nom>=<url> [...]
import AxeBuilder from '@axe-core/playwright';
import { chromium } from '@playwright/test';

const apps = process.argv.slice(2).map((a) => [a.slice(0, a.indexOf('=')), a.slice(a.indexOf('=') + 1)]);
const tags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];
const summarize = (r) => r.violations.map((v) => `${v.id}(${v.impact},${v.nodes.length})`);
const out = {};
const browser = await chromium.launch();
for (const [name, url] of apps) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();
  await page.goto(url);
  await page.getByText('worker-1', { exact: true }).filter({ visible: true }).first().waitFor();
  const r = { lang: await page.evaluate(() => document.documentElement.lang || null) };
  r.axePage = summarize(await new AxeBuilder({ page }).withTags(tags).analyze());
  const opener = page.getByRole('button', { name: 'Rattacher un worker' }).first();
  await opener.focus();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog');
  r.dialogRole = await dialog.first().isVisible().catch(() => false);
  await page.waitForTimeout(400);
  r.dialogAccessibleName = r.dialogRole ? await dialog.first().getAttribute('aria-labelledby').then(async (id) =>
    id ? (await page.locator(`#${id}`).innerText().catch(() => null)) : await dialog.first().getAttribute('aria-label')) : null;
  r.focusInDialogOnOpen = await page.evaluate(() => !!document.activeElement?.closest('[role=dialog],dialog'));
  r.axeDialog = summarize(await new AxeBuilder({ page }).withTags(tags).analyze());
  let escaped = false;
  for (let i = 0; i < 8; i++) {
    await page.keyboard.press('Tab');
    if (!(await page.evaluate(() => !!document.activeElement?.closest('[role=dialog],dialog')))) escaped = true;
  }
  r.focusTrapped = !escaped;
  await page.keyboard.press('Escape');
  await page.waitForTimeout(400);
  r.escapeCloses = !(await dialog.first().isVisible().catch(() => false));
  r.focusReturnsToOpener = await opener.evaluate((el) => el === document.activeElement);
  // Approbation complète au clavier.
  await opener.focus(); await page.keyboard.press('Enter'); await page.waitForTimeout(400);
  const input = page.getByLabel('Clé de jumelage').first();
  r.inputLabelled = await input.isVisible().catch(() => false);
  if (r.inputLabelled) {
    await input.fill('good-key');
    await page.getByRole('button', { name: 'Approuver' }).first().focus();
    await page.keyboard.press('Enter');
    r.successAnnounced = await page.getByText(/a rejoint le central/).first().waitFor({ timeout: 5000 }).then(() => true, () => false);
    r.successLiveRegion = await page.waitForFunction(() => [...document.querySelectorAll('[aria-live],[role=status],[role=alert]')]
      .some((n) => /a rejoint le central/.test(n.textContent ?? '')), null, { timeout: 3000 }).then(() => true, () => false);
  }
  out[name] = r;
  console.error(name, JSON.stringify(r));
  await context.close();
}
await browser.close();
console.log(JSON.stringify(out, null, 2));
