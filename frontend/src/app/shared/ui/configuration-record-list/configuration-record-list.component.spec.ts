import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { ConfigurationRecordListComponent } from './configuration-record-list.component';
import { ComponentConfigurationFormComponent } from '../component-configuration-form.component';

describe('Declarative record lists', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient()] }));
  it('includes active advanced restrictions in a folded rule', async () => {
    const fixture = TestBed.createComponent(ConfigurationRecordListComponent);
    fixture.componentRef.setInput('field', {
      id: 'selection',
      type: 'record-list',
      label: 'Sélection',
      itemFields: [
        { id: 'languages', type: 'text', label: 'Langues' },
        { id: 'codecs', type: 'text', label: 'Codecs', importance: 'advanced', defaultValue: '' },
        {
          id: 'excludeCommentary',
          type: 'select',
          label: 'Exclure les commentaires',
          importance: 'advanced',
          defaultValue: 'no',
          options: [
            { value: 'yes', label: 'Oui' },
            { value: 'no', label: 'Non' },
          ],
        },
      ],
    });
    fixture.componentRef.setInput('value', [
      { languages: 'fra', codecs: 'dts', excludeCommentary: 'yes' },
    ]);
    await fixture.whenStable();
    const summary = fixture.nativeElement.querySelector('summary').textContent;
    expect(summary).toContain('Codecs : dts');
    expect(summary).toContain('Exclure les commentaires : Oui');
    expect(fixture.nativeElement.querySelectorAll('input').length).toBe(1);
  });
  it('adds, edits and submits a structured rule using the common form', async () => {
    const fixture = TestBed.createComponent(ComponentConfigurationFormComponent);
    fixture.componentRef.setInput('schema', {
      version: 1,
      fields: [
        {
          id: 'rules',
          type: 'record-list',
          label: 'Règles audio',
          minItems: 1,
          maxItems: 2,
          itemFields: [
            { id: 'languages', type: 'text', label: 'Langues', defaultValue: 'fra' },
            {
              id: 'codec',
              type: 'select',
              label: 'Codec',
              defaultValue: 'ac3',
              options: [{ value: 'ac3', label: 'AC-3' }],
            },
          ],
        },
      ],
    });
    const saved = vi.fn();
    fixture.componentInstance.saved.subscribe(saved);
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).not.toContain('Autres critères de sélection');
    expect(fixture.nativeElement.querySelector('button[type=submit]').disabled).toBe(true);
    const add = [...fixture.nativeElement.querySelectorAll('button')].find((button) =>
      (button as HTMLElement).textContent?.includes('Ajouter une règle'),
    ) as HTMLButtonElement;
    add.click();
    await fixture.whenStable();
    const input = fixture.nativeElement.querySelector('input');
    input.value = 'jpn';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    fixture.nativeElement.querySelector('form').dispatchEvent(new Event('submit'));
    expect(saved).toHaveBeenCalledWith({ rules: [{ languages: 'jpn', codec: 'ac3' }] });
  });
});
