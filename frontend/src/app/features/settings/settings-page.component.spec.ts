import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Subject } from 'rxjs';
import { SettingsPageComponent, settingsDomain } from './settings-page.component';

describe('SettingsPageComponent', () => {
  async function setup(query = '') {
    await TestBed.configureTestingModule({
      imports: [SettingsPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    await TestBed.inject(Router).navigateByUrl('/' + query);
    const fixture = TestBed.createComponent(SettingsPageComponent);
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }
  it('shows six domains, a single editing drawer, and appearance by default', async () => {
    const { element } = await setup();
    expect(
      [...element.querySelectorAll('[role="tab"]')].map((tab) => tab.textContent?.trim()),
    ).toEqual(['Interface', 'Accès', 'IA', 'Connexions', 'Réseau', 'Exploitation']);
    expect(element.querySelector('app-appearance-tab')).not.toBeNull();
    expect(element.querySelectorAll('app-detail-drawer')).toHaveLength(1);
  });
  it.each(['tokens', 'users', 'security', 'access'])(
    'keeps legacy access links working: %s',
    async (tab) => {
      const { element } = await setup(`?tab=${tab}`);
      expect(element.querySelector('app-user-management-tab')).not.toBeNull();
      expect(element.querySelector('app-api-token-tab')).not.toBeNull();
    },
  );
  it('groups the AI engine, providers and MCP in the IA domain, with no sub-tabs', async () => {
    const { element } = await setup('?tab=ai');
    expect(element.querySelector('app-ai-tools-tab')).not.toBeNull();
    expect(element.querySelector('app-mcp-server-tab')).not.toBeNull();
    expect(element.querySelector('app-webhooks-tab')).toBeNull();
    expect(element.querySelectorAll('[role="tab"]')).toHaveLength(6);
  });
  it.each(['connections', 'integrations', 'webhooks', 'metrics'])(
    'keeps webhooks and Prometheus under Connexions: %s',
    async (tab) => {
      const { element } = await setup(`?tab=${tab}`);
      expect(element.querySelector('app-webhooks-tab')).not.toBeNull();
      expect(element.querySelector('app-metrics-tab')).not.toBeNull();
      expect(element.querySelector('app-ai-tools-tab')).toBeNull();
    },
  );
  it.each(['system', 'logs', 'system-logs', 'overview', 'operations'])(
    'keeps operation deep links working: %s',
    async (tab) => {
      const { element } = await setup(`?tab=${tab}`);
      expect(element.querySelector('app-system-tab')).not.toBeNull();
      expect(element.querySelector('app-system-log-viewer')).not.toBeNull();
      expect(element.querySelector('app-appearance-tab')).toBeNull();
    },
  );
  it.each([false, true])(
    'protects unsaved changes before a tab switch (confirmed: %s)',
    async (confirmed) => {
      const { fixture, element } = await setup();
      const decision = new Subject<boolean>();
      vi.spyOn(fixture.componentInstance, 'hasUnsavedChanges').mockReturnValue(true);
      const open = vi
        .spyOn(fixture.debugElement.injector.get(MatDialog), 'open')
        .mockReturnValue({ afterClosed: () => decision } as unknown as MatDialogRef<unknown>);
      const change = fixture.componentInstance['onMainTabChange']('operations');
      expect(open).toHaveBeenCalledOnce();
      expect(element.querySelector('app-system-tab')).toBeNull();
      decision.next(confirmed);
      decision.complete();
      await change;
      fixture.detectChanges();
      expect(element.querySelector('app-system-tab') !== null).toBe(confirmed);
    },
  );
  it('maps unknown links to Interface', () => expect(settingsDomain('unknown')).toBe('interface'));
});
