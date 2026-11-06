import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { MatChipListbox, MatChipListboxChange } from '@angular/material/chips';
import { CategoryTabsComponent } from './category-tabs.component';

describe('CategoryTabsComponent', () => {
  const options = [
    { value: 'media', label: 'Médias', count: 4 },
    { value: 'scripts', label: 'Scripts', count: 2 },
    { value: 'encoding', label: 'Profils d’encodage' },
  ] as const;

  it('renders a labelled chip listbox with the selected option', () => {
    const fixture = TestBed.createComponent(CategoryTabsComponent);
    fixture.componentRef.setInput('label', 'Type de librairie');
    fixture.componentRef.setInput('options', options);
    fixture.componentRef.setInput('selected', 'scripts');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const listbox = host.querySelector('mat-chip-listbox');
    expect(listbox).toBeTruthy();
    expect(listbox?.getAttribute('aria-label')).toBe('Type de librairie');

    const chips = Array.from(host.querySelectorAll('mat-chip-option')) as HTMLElement[];
    expect(chips.length).toBe(3);
    expect(chips[0].getAttribute('aria-selected')).toBe('false');
    expect(chips[1].getAttribute('aria-selected')).toBe('true');
    expect(chips[2].getAttribute('aria-selected')).toBe('false');
  });

  it('emits the new value when the listbox selection changes', () => {
    const fixture = TestBed.createComponent(CategoryTabsComponent);
    fixture.componentRef.setInput('label', 'Type de librairie');
    fixture.componentRef.setInput('options', options);
    fixture.componentRef.setInput('selected', 'media');
    fixture.detectChanges();

    const emitted: string[] = [];
    fixture.componentInstance.selectedChange.subscribe((val) => emitted.push(val));

    const listbox = fixture.debugElement.query(By.directive(MatChipListbox))
      .componentInstance as MatChipListbox;
    listbox.change.emit({ value: 'encoding' } as MatChipListboxChange);
    expect(emitted.at(-1)).toBe('encoding');
  });
});
