import { TestBed } from '@angular/core/testing';
import { CategoryTabsComponent } from './category-tabs.component';

describe('CategoryTabsComponent', () => {
  const options = [
    { value: 'media', label: 'Médias', count: 4 },
    { value: 'scripts', label: 'Scripts', count: 2 },
    { value: 'encoding', label: 'Profils d’encodage' },
  ] as const;

  it('renders a labelled group of filter buttons with the selected state', () => {
    const fixture = TestBed.createComponent(CategoryTabsComponent);
    fixture.componentRef.setInput('label', 'Type de librairie');
    fixture.componentRef.setInput('options', options);
    fixture.componentRef.setInput('selected', 'scripts');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const tablist = host.querySelector('[role="group"]');
    expect(tablist).toBeTruthy();
    expect(tablist?.getAttribute('aria-label')).toBe('Type de librairie');

    const tabs = Array.from(host.querySelectorAll('button')) as HTMLButtonElement[];
    expect(tabs.length).toBe(3);
    expect(tabs[0].getAttribute('aria-pressed')).toBe('false');
    expect(tabs[1].getAttribute('aria-pressed')).toBe('true');
    expect(tabs[2].getAttribute('aria-pressed')).toBe('false');
  });

  it('supports ArrowRight, ArrowLeft, Home and End keyboard navigation', () => {
    const fixture = TestBed.createComponent(CategoryTabsComponent);
    fixture.componentRef.setInput('label', 'Type de librairie');
    fixture.componentRef.setInput('options', options);
    fixture.componentRef.setInput('selected', 'media');
    fixture.detectChanges();

    const emitted: string[] = [];
    fixture.componentInstance.selectedChange.subscribe((val) => emitted.push(val));

    const tabs = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('button'),
    ) as HTMLButtonElement[];

    tabs[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    expect(emitted.at(-1)).toBe('scripts');

    tabs[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
    expect(emitted.at(-1)).toBe('encoding');

    tabs[2].dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true }));
    expect(emitted.at(-1)).toBe('media');

    tabs[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    expect(emitted.at(-1)).toBe('encoding');
  });
});
