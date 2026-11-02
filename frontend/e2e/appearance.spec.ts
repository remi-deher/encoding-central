import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

async function openAppearancePalette(page: import('@playwright/test').Page) {
  const appearance = page.getByRole('button', { name: 'Apparence : thème et palette de couleurs' });
  if (!(await appearance.isVisible().catch(() => false))) {
    await page.getByRole('button', { name: 'Plus', exact: true }).click();
  }
  await appearance.click();
  return appearance;
}

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: {
        setupRequired: false,
        user: { id: 'admin', username: 'admin', role: 'admin' },
      },
    }),
  );
  await page.route('**/api/v1/tools', (route) =>
    route.fulfill({
      json: [
        {
          id: 'ffmpeg',
          name: 'FFmpeg',
          role: 'Encodage vidéo et audio',
          provider: 'stock',
          stockVersion: '8',
          conformanceStatus: 'ready',
          probePreserved: true,
          executables: ['ffmpeg'],
          mediaTypes: ['video', 'audio'],
          encoders: [],
          clusterReadiness: 'Disponible',
        },
      ],
    }),
  );
});

test('palette menu changes Material and application colours, preserves mode and survives reload', async ({
  page,
}) => {
  await page.goto('/');
  const appearance = await openAppearancePalette(page);
  const panel = page.getByRole('dialog', { name: 'Apparence' });
  await expect(panel).toBeVisible();
  const panelBox = await panel.boundingBox();
  const viewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight }));
  expect(panelBox).not.toBeNull();
  expect(panelBox!.x).toBeGreaterThanOrEqual(0);
  expect(panelBox!.y).toBeGreaterThanOrEqual(0);
  expect(panelBox!.x + panelBox!.width).toBeLessThanOrEqual(viewport.width);
  expect(panelBox!.y + panelBox!.height).toBeLessThanOrEqual(viewport.height);
  await page.getByRole('button', { name: 'Iris', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-palette', 'iris');
  await page.getByRole('radio', { name: 'Sombre' }).click();
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-palette', 'iris');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  const reloadedAppearance = await openAppearancePalette(page);
  await expect(page.getByRole('button', { name: 'Iris', exact: true })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  await page.keyboard.press('Escape');
  await expect(reloadedAppearance).toBeFocused();
});

test('populated tools stay readable in four palettes and both modes without mobile overflow', async ({
  page,
}, info) => {
  await page.goto('/plugins');
  await page.getByRole('tab', { name: /Outils média/ }).click();
  await expect(page.getByRole('heading', { name: 'FFmpeg', exact: true })).toBeVisible();
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const colours: string[] = [];
  for (const palette of ['forest', 'ocean', 'iris', 'rose']) {
    await openAppearancePalette(page);
    await page
      .getByRole('button', {
        name: { forest: 'Forêt', ocean: 'Océan', iris: 'Iris', rose: 'Rose' }[palette],
        exact: true,
      })
      .click();
    await page.keyboard.press('Escape');
    colours.push(
      await page.evaluate(() =>
        getComputedStyle(document.documentElement).getPropertyValue('--mat-sys-primary'),
      ),
    );
    for (const mode of ['light', 'dark']) {
      await page.evaluate((value) => (document.documentElement.dataset['theme'] = value), mode);
      await page.evaluate(async () => {
        await Promise.all(
          document.getAnimations().map((animation) => animation.finished.catch(() => undefined)),
        );
      });
      const result = await new AxeBuilder({ page })
        .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
        .analyze();
      expect(
        result.violations.map((item) => ({
          id: item.id,
          targets: item.nodes.map((node) => node.target),
        })),
      ).toEqual([]);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
        true,
      );
      await page.screenshot({
        path: info.outputPath(`tools-${palette}-${mode}.png`),
        fullPage: true,
      });
    }
  }
  expect(new Set(colours).size).toBe(4);
  await page.setViewportSize({ width: 320, height: 800 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await expect(page.getByRole('heading', { name: 'FFmpeg', exact: true })).toBeVisible();
});

test('adaptive shell keeps all destinations reachable at tablet width and supports pointer search', async ({
  page,
}) => {
  await page.setViewportSize({ width: 900, height: 900 });
  await page.goto('/');
  await expect(page.locator('.application-sidebar')).toHaveClass(/is-collapsed/);
  await expect(page.getByRole('link', { name: 'Paramètres', exact: true })).toBeVisible();
  const search = page.getByRole('combobox', { name: /Rechercher dans/ });
  await search.fill('Profils');
  await search.press('Enter');
  await expect(page).toHaveURL(/libraries\/encoding-profiles/);
});

test('store cards and repository actions stay accessible with populated content on narrow screens', async ({
  page,
}) => {
  await page.route('**/api/v1/store/repositories', (route) =>
    route.fulfill({
      json: [
        {
          id: 'repo',
          name: 'Dépôt de test',
          url: 'https://example.org/plugins/repository.json',
          description: '',
          isOfficial: false,
          isLocked: false,
          enabled: true,
          packagesCount: 1,
        },
      ],
    }),
  );
  await page.route('**/api/v1/store/packages', (route) =>
    route.fulfill({
      json: [
        {
          id: 'pkg',
          name: 'Outil de test',
          kind: 'tool',
          category: 'tools',
          categoryLabel: 'Outil',
          version: '1.0',
          installedVersion: null,
          hasUpdate: false,
          author: 'Tests',
          description: 'Un outil de démonstration pour vérifier le rendu du Store.',
          requiredTools: [],
          repositoryId: 'repo',
          icon: '◇',
        },
      ],
    }),
  );
  await page.goto('/plugins');
  await page.getByRole('tab', { name: /Store & Dépôts/ }).click();
  await expect(page.getByRole('heading', { name: 'Outil de test' })).toBeVisible();
  const importAction = page.getByRole('button', { name: 'Importer une archive', exact: true });
  await expect(importAction).toBeVisible();
  const [archiveChooser] = await Promise.all([
    page.waitForEvent('filechooser'),
    importAction.click(),
  ]);
  expect(archiveChooser.isMultiple()).toBe(false);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  for (const mode of ['light', 'dark']) {
    await page.evaluate((value) => (document.documentElement.dataset['theme'] = value), mode);
    await page.evaluate(async () => {
      await Promise.all(
        document.getAnimations().map((animation) => animation.finished.catch(() => undefined)),
      );
    });
    const audit = await new AxeBuilder({ page })
      .include('app-plugin-catalog-page')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
      .analyze();
    expect(audit.violations.map((item) => item.id)).toEqual([]);
  }
  await page.setViewportSize({ width: 320, height: 800 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
