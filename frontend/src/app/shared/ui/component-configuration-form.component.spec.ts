import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { ComponentConfigurationFormComponent } from './component-configuration-form.component';

describe('Flow inspector progressive disclosure', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient()] }));

  it('preserves historical and unknown values instead of silently migrating the flow', async () => {
    const fixture = TestBed.createComponent(ComponentConfigurationFormComponent);
    fixture.componentRef.setInput('schema', {
      version: 1,
      fields: [
        { id: 'mode', label: 'Mode', type: 'text' },
        { id: 'mediaPolicy', label: 'Publication historique', type: 'text', legacyOnly: true },
      ],
    });
    fixture.componentRef.setInput('values', {
      mode: 'timer',
      mediaPolicy: 'beside',
      extension: 'preserved',
    });
    const saved = vi.fn();
    fixture.componentInstance.saved.subscribe(saved);
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Publication historique');
    fixture.nativeElement.querySelector('form').dispatchEvent(new Event('submit'));
    expect(saved).toHaveBeenCalledWith({
      mode: 'timer',
      mediaPolicy: 'beside',
      extension: 'preserved',
    });
    fixture.componentRef.setInput('values', { mode: 'timer' });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).not.toContain('Publication historique');
  });

  it('keeps a condition editor in the primary settings even when the schema does not mark it inline', async () => {
    const fixture = TestBed.createComponent(ComponentConfigurationFormComponent);
    fixture.componentRef.setInput('progressiveDisclosure', true);
    fixture.componentRef.setInput('schema', {
      version: 1,
      fields: [
        { id: 'rules', label: 'Règles du média', type: 'condition-tree', conditionFields: [] },
        { id: 'note', label: 'Note', type: 'text', importance: 'advanced' },
      ],
    });
    await fixture.whenStable();
    const editor = fixture.nativeElement.querySelector('app-condition-tree-editor') as HTMLElement;
    expect(editor).not.toBeNull();
    expect(editor.closest('details')).toBeNull();
    expect(fixture.nativeElement.querySelector('details').textContent).toContain('Note');
  });

  it('shows optional business options and preserves hidden mode values', async () => {
    const fixture = TestBed.createComponent(ComponentConfigurationFormComponent);
    fixture.componentRef.setInput('progressiveDisclosure', true);
    fixture.componentRef.setInput('schema', {
      version: 1,
      fields: [
        {
          id: 'mode',
          label: 'Mode',
          type: 'select',
          options: [{ value: 'timer', label: 'Délai' }],
          defaultValue: 'timer',
        },
        { id: 'rate', label: 'Fréquence', type: 'number', defaultValue: 48000 },
        {
          id: 'node',
          label: 'Worker',
          type: 'text',
          visibleWhenFieldId: 'mode',
          visibleWhenValue: 'node',
        },
      ],
    });
    fixture.componentRef.setInput('values', { node: 'saved-node' });
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('details')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Fréquence');
    expect(fixture.nativeElement.textContent).not.toContain('Worker');
    const saved = vi.fn();
    fixture.componentInstance.saved.subscribe(saved);
    fixture.nativeElement.querySelector('form').dispatchEvent(new Event('submit'));
    expect(saved.mock.calls[0][0].node).toBe('saved-node');
  });

  it('reveals an invalid advanced option so a disabled submit can be corrected', async () => {
    const fixture = TestBed.createComponent(ComponentConfigurationFormComponent);
    fixture.componentRef.setInput('progressiveDisclosure', true);
    fixture.componentRef.setInput('schema', {
      version: 1,
      fields: [
        { id: 'limit', label: 'Limite', type: 'number', min: 1, max: 10, importance: 'advanced' },
      ],
    });
    fixture.componentRef.setInput('values', { limit: 99 });
    await fixture.whenStable();
    const advanced = fixture.nativeElement.querySelector('details') as HTMLDetailsElement;
    expect(advanced.open).toBe(true);
    const input = advanced.querySelector('input')!;
    input.value = '5';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    expect(
      (fixture.nativeElement.querySelector('button[type=submit]') as HTMLButtonElement).disabled,
    ).toBe(false);
  });
});
