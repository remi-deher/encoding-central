const { chromium, expect } = require('@playwright/test');
const { AxeBuilder } = require('@axe-core/playwright');
(async () => {
  const browser = await chromium.launch({ args: ['--no-sandbox'] });
  try {
    const context = await browser.newContext({
      viewport: { width: 1440, height: 1000 },
      colorScheme: 'dark',
    });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', (error) => errors.push(error.message));
    await page.goto('http://127.0.0.1:4319');
    const launcher = page.getByRole('button', { name: 'Ouvrir l’assistant IA', exact: true });
    await expect(launcher).toBeVisible();
    await page.screenshot({ path: 'mockups/ai-widget/closed.png', fullPage: true });
    await launcher.click();
    const widget = page.getByRole('region', { name: 'Assistant IA', exact: true });
    await expect(widget).toBeVisible();
    await page.screenshot({ path: 'mockups/ai-widget/desktop.png', fullPage: true });
    await page.getByRole('combobox', { name: 'Modèle', exact: true }).click();
    await page.getByRole('option', { name: 'Claude Opus', exact: true }).click();
    await page.getByRole('button', { name: 'Régler le raisonnement' }).click();
    await page.getByRole('menuitem', { name: 'Maximum', exact: true }).click();
    await page.getByRole('button', { name: 'Joindre une ressource' }).click();
    await page.getByRole('menuitem', { name: 'Nœud · Maison' }).click();
    await page
      .getByRole('textbox', { name: 'Votre message' })
      .fill('Pourquoi ce nœud est-il hors ligne ?');
    await page.getByRole('textbox', { name: 'Votre message' }).press('Enter');
    await expect(
      widget.getByText('Pourquoi ce nœud est-il hors ligne ?', { exact: true }),
    ).toBeVisible();
    await page.getByRole('button', { name: 'Réduire la conversation' }).click();
    await expect(widget).toHaveCount(0);
    await expect(launcher).toBeFocused();
    await launcher.click();
    await expect(
      widget.getByText('Pourquoi ce nœud est-il hors ligne ?', { exact: true }),
    ).toBeVisible();
    await page.screenshot({ path: 'mockups/ai-widget/conversation.png', fullPage: true });
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 844 });
      await expect
        .poll(async () => (await widget.boundingBox())?.x ?? -1)
        .toBeGreaterThanOrEqual(15);
      for (const theme of ['dark', 'light']) {
        await page.evaluate(
          (value) => document.documentElement.setAttribute('data-theme', value),
          theme,
        );
        await page.screenshot({ path: `mockups/ai-widget/${width}-${theme}.png`, fullPage: true });
        const rect = await widget.boundingBox();
        if (
          !rect ||
          rect.x < 0 ||
          rect.x + rect.width > width ||
          rect.y < 0 ||
          rect.y + rect.height > 844
        )
          throw Error('Widget outside viewport');
        const result = await new AxeBuilder({ page })
          .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
          .analyze();
        if (result.violations.length)
          throw Error(
            JSON.stringify(
              result.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })),
            ),
          );
      }
    }
    await page.getByRole('textbox', { name: 'Votre message' }).press('Escape');
    await expect(widget).toHaveCount(0);
    if (errors.length) throw Error(errors.join('\n'));
    console.log(
      'Widget : modèles, raisonnement, pièce jointe, saisie, réduction, mobile et WCAG AA OK',
    );
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
