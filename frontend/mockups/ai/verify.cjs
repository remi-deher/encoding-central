const { chromium, expect } = require('@playwright/test');
(async () => {
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
  try {
    const context = await browser.newContext({
      viewport: { width: 1600, height: 1100 },
      colorScheme: 'dark',
    });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', (error) => errors.push(error.message));
    await page.goto('http://127.0.0.1:4318');
    await page.getByRole('heading', { name: 'Intelligence artificielle', exact: true }).waitFor();
    await page.screenshot({ path: 'mockups/ai/desktop.png', fullPage: true });
    for (const model of ['Claude Opus', 'Claude Sonnet', 'GPT 6.1']) {
      await page.getByRole('combobox', { name: 'Modèle de la conversation' }).click();
      await page.getByRole('option', { name: model, exact: true }).click();
      await expect(page.getByRole('combobox')).toContainText(model);
    }
    await expect(page.getByText('Conversations privées', { exact: true })).toHaveCount(0);
    await page
      .getByRole('textbox', { name: 'Votre message à l’assistant' })
      .fill('Expliquer mon flow HEVC');
    await page.getByRole('button', { name: 'Envoyer' }).click();
    await page.getByText('Message simulé.', { exact: false }).waitFor();
    await page.getByRole('combobox', { name: 'Modèle de la conversation' }).click();
    await page.getByRole('option', { name: 'Claude Opus', exact: true }).click();
    await page.getByRole('tab', { name: 'Fournisseurs' }).click();
    await page.getByRole('heading', { name: 'Vos fournisseurs IA' }).waitFor();
    await page.getByRole('switch', { name: 'Claude Opus', exact: true }).click();
    await expect(page.getByRole('switch', { name: 'Claude Opus', exact: true })).not.toBeChecked();
    await page.getByRole('tab', { name: 'Assistant', exact: true }).click();
    await page.getByRole('combobox').click();
    if (await page.getByRole('option', { name: 'Claude Opus', exact: true }).count())
      throw Error('Disabled model is still selectable');
    await page.keyboard.press('Escape');
    await page.getByRole('tab', { name: 'Fournisseurs' }).click();
    await page.getByRole('switch', { name: 'Claude Opus', exact: true }).click();
    await expect(page.getByRole('switch', { name: 'Claude Opus', exact: true })).toBeChecked();
    if (await page.getByRole('heading', { name: 'Ollama', exact: true }).count())
      throw Error('Ollama should not be configured');

    await page.getByRole('tab', { name: 'Activité' }).click();
    await page.getByRole('button', { name: 'Ouvrir la conversation' }).click();
    await page.getByRole('button', { name: 'Retirer Flow · HEVC · v3', exact: true }).click();
    await page.getByRole('button', { name: 'Joindre une ressource' }).click();
    for (const resource of ['Flow · HEVC · v3', 'Nœud · Maison']) {
      await page.getByRole('combobox', { name: 'Ressource', exact: true }).click();
      await page.getByRole('option', { name: resource, exact: true }).click();
      await page.getByRole('button', { name: 'Joindre', exact: true }).click();
      await expect(
        page.getByRole('button', { name: 'Retirer ' + resource, exact: true }),
      ).toBeVisible();
    }
    await page.getByRole('button', { name: 'Joindre une ressource' }).click();
    await page.getByRole('button', { name: 'Nouvelle conversation' }).click();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: 'mockups/ai/mobile.png', fullPage: true });
    if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth))
      throw Error('Mobile overflow');
    await page.getByRole('button', { name: 'Passer au thème clair' }).click();
    await page.screenshot({ path: 'mockups/ai/mobile-light.png', fullPage: true });
    const { AxeBuilder } = require('@axe-core/playwright');
    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
      .analyze();
    if (results.violations.length)
      throw Error(
        JSON.stringify(
          results.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })),
        ),
      );
    if (errors.length) throw Error(errors.join('\n'));
    console.log('Navigation, saisie, contexte, mobile, thèmes et accessibilité : OK');
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
