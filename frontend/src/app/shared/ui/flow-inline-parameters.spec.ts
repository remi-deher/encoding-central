import { TestBed } from '@angular/core/testing';
import { FlowInlineParameters } from './flow-inline-parameters';

describe('Inline flow parameters', () => {
  it('hides a historical track index when selection criteria apply and retains its value', async () => {
    const fixture = TestBed.createComponent(FlowInlineParameters);
    fixture.componentRef.setInput('schema', {
      version: 1,
      fields: [
        { id: 'selection', type: 'record-list', label: 'Sélection', itemFields: [] },
        {
          id: 'trackId',
          type: 'number',
          label: 'Indice de piste',
          inline: true,
          visibleWhenFieldId: 'selection',
          visibleWhenEmpty: true,
        },
      ],
    });
    fixture.componentRef.setInput('values', { trackId: 2, selection: [{ languages: 'fra' }] });
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('input')).toBeNull();
    fixture.componentRef.setInput('values', { trackId: 2, selection: [] });
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('input').value).toBe('2');
    expect(changed).not.toHaveBeenCalled();
  });
});
