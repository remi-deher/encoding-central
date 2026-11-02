import { TestBed } from '@angular/core/testing';
import { SearchFieldComponent } from './search-field.component';

describe('SearchFieldComponent', () => {
  function create(value = '') {
    const fixture = TestBed.createComponent(SearchFieldComponent);
    fixture.componentRef.setInput('label', 'Rechercher un flow');
    fixture.componentRef.setInput('value', value);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, input: host.querySelector('input') as HTMLInputElement };
  }

  it('labels the input and emits typed values', () => {
    const { fixture, input } = create();
    const emitted: string[] = [];
    fixture.componentInstance.valueChange.subscribe((v) => emitted.push(v));

    expect(input.getAttribute('aria-label')).toBe('Rechercher un flow');
    input.value = 'hevc';
    input.dispatchEvent(new Event('input'));
    expect(emitted).toEqual(['hevc']);
  });

  it('shows a clear button only when there is a value and clears on click or Escape', () => {
    expect(create().host.querySelector('.search-field-clear')).toBeNull();

    const { fixture, host, input } = create('abc');
    const emitted: string[] = [];
    fixture.componentInstance.valueChange.subscribe((v) => emitted.push(v));
    (host.querySelector('.search-field-clear') as HTMLButtonElement).click();
    input.value = 'abc';
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(emitted).toEqual(['', '']);
  });

  it('opens a filter panel from the Filtres button and closes it on Escape', () => {
    const fixture = TestBed.createComponent(SearchFieldComponent);
    fixture.componentRef.setInput('label', 'Rechercher');
    fixture.componentRef.setInput('filterCount', 2);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const button = host.querySelector('.search-field-filters') as HTMLButtonElement;
    const panel = host.querySelector('.search-field-panel') as HTMLElement;

    expect(button.textContent).toContain('2');
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(panel.hasAttribute('inert')).toBe(true);

    button.click();
    fixture.detectChanges();
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(panel.hasAttribute('inert')).toBe(false);

    host.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();
    expect(fixture.componentInstance.filtersOpen()).toBe(false);
  });

  it('has no filter button without filterCount', () => {
    expect(create().host.querySelector('.search-field-filters')).toBeNull();
  });
});
