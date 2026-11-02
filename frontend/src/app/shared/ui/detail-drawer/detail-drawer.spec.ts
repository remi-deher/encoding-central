import { TestBed } from '@angular/core/testing';
import { DetailDrawerComponent } from './detail-drawer';

describe('DetailDrawerComponent', () => {
  let sidebar: HTMLElement;

  beforeEach(() => {
    sidebar = document.createElement('aside');
    sidebar.className = 'application-sidebar';
    document.body.appendChild(sidebar);
  });

  afterEach(() => {
    sidebar.remove();
    document.body.classList.remove('detail-drawer-open');
  });

  function render(inputs: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(DetailDrawerComponent);
    for (const [key, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(key, value);
    }
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('toggles detail-drawer-open class on body and inert on sidebar when modal is true and opened', () => {
    const { fixture } = render({ modal: true, opened: false });
    expect(document.body.classList.contains('detail-drawer-open')).toBe(false);
    expect(sidebar.hasAttribute('inert')).toBe(false);

    fixture.componentRef.setInput('opened', true);
    fixture.detectChanges();

    expect(document.body.classList.contains('detail-drawer-open')).toBe(true);
    expect(sidebar.hasAttribute('inert')).toBe(true);

    fixture.componentRef.setInput('opened', false);
    fixture.detectChanges();

    expect(document.body.classList.contains('detail-drawer-open')).toBe(false);
    expect(sidebar.hasAttribute('inert')).toBe(false);
  });

  it('does not toggle class or inert when modal is false', () => {
    const { fixture } = render({ modal: false, opened: true });
    expect(document.body.classList.contains('detail-drawer-open')).toBe(false);
    expect(sidebar.hasAttribute('inert')).toBe(false);
  });

  it('removes class and inert upon component destruction', () => {
    const { fixture } = render({ modal: true, opened: true });
    expect(document.body.classList.contains('detail-drawer-open')).toBe(true);
    expect(sidebar.hasAttribute('inert')).toBe(true);

    fixture.destroy();

    expect(document.body.classList.contains('detail-drawer-open')).toBe(false);
    expect(sidebar.hasAttribute('inert')).toBe(false);
  });
});

describe('DetailDrawerComponent header template', () => {
  function render(inputs: Record<string, unknown>) {
    const fixture = TestBed.createComponent(DetailDrawerComponent);
    for (const [key, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(key, value);
    }
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows no icon for the neutral variant and the variant glyph otherwise', () => {
    expect(render({ opened: true }).querySelector('.drawer-icon')).toBeNull();
    const danger = render({ opened: true, variant: 'danger' });
    expect(danger.querySelector('.drawer-icon')?.textContent?.trim()).toBe('!');
    expect(danger.querySelector('.drawer-header_danger')).not.toBeNull();
  });

  it('prefers an explicit icon over the variant glyph', () => {
    const element = render({ opened: true, variant: 'info', icon: '+' });
    expect(element.querySelector('.drawer-icon')?.textContent?.trim()).toBe('+');
  });

  it('exposes the step progress to assistive technology', () => {
    const element = render({ opened: true, step: { current: 1, total: 2 } });
    const bar = element.querySelector('.drawer-progress');
    expect(bar?.getAttribute('aria-label')).toBe('Étape 1 sur 2');
    expect(bar?.getAttribute('aria-valuenow')).toBe('1');
  });
});

describe('DetailDrawerComponent modal ownership and protected dismissal', () => {
  it('keeps the document locked until the last modal closes, including destruction of a closed drawer', () => {
    const first = TestBed.createComponent(DetailDrawerComponent);
    const second = TestBed.createComponent(DetailDrawerComponent);
    for (const fixture of [first, second]) {
      fixture.componentRef.setInput('modal', true);
      fixture.componentRef.setInput('opened', true);
      fixture.detectChanges();
    }
    first.componentRef.setInput('opened', false);
    first.detectChanges();
    first.destroy();
    expect(document.body.classList.contains('detail-drawer-open')).toBe(true);
    second.destroy();
    expect(document.body.classList.contains('detail-drawer-open')).toBe(false);
  });

  it('requests confirmation without closing a guarded drawer from the close button', () => {
    const fixture = TestBed.createComponent(DetailDrawerComponent);
    fixture.componentRef.setInput('opened', true);
    fixture.componentRef.setInput('closeGuarded', true);
    fixture.detectChanges();
    let requested = 0;
    fixture.componentInstance.closeRequested.subscribe(() => requested++);
    const drawer = fixture.nativeElement.querySelector('mat-drawer');
    fixture.nativeElement.querySelector('.drawer-close').click();
    fixture.detectChanges();
    expect(requested).toBe(1);
    expect(drawer.classList.contains('mat-drawer-opened')).toBe(true);
    drawer.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(requested).toBe(2);
  });
});
