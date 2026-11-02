import { TestBed } from '@angular/core/testing';
import { ResourceCardComponent } from './resource-card.component';

describe('ResourceCardComponent', () => {
  function render(inputs: Record<string, unknown>) {
    const fixture = TestBed.createComponent(ResourceCardComponent);
    fixture.componentRef.setInput('title', 'encoding-51');
    fixture.componentRef.setInput('statusLabel', 'En ligne');
    for (const [key, value] of Object.entries(inputs)) fixture.componentRef.setInput(key, value);
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('shows title, status and badges', () => {
    const { element } = render({
      statusTone: 'success',
      badges: ['Serveur', 'Worker'],
      acceleratorBadges: [{ label: 'NVENC', tone: 'lime' }],
      osFamily: 'linux',
      osLabel: 'Ubuntu 24.04',
    });
    expect(element.querySelector('.resource-card-title')!.textContent).toContain('encoding-51');
    expect(element.textContent).toContain('En ligne');
    expect(element.querySelectorAll('.resource-card-badge').length).toBe(3);
    expect(element.querySelector('.resource-card-badge_accelerator')!.textContent).toContain('NVENC');
    expect(element.querySelector('.resource-card-os-icon')).toBeTruthy();
    expect(element.querySelector('.resource-card-os-icon')!.getAttribute('data-os')).toBe('linux');
  });

  it('shows at most three tags, highlighted ones first, and counts the rest', () => {
    const tags = [
      { label: 'ffmpeg 6.1' },
      { label: 'mkvtoolnix 82' },
      { label: 'mediainfo 24' },
      { label: 'GPU Intel', highlighted: true },
      { label: 'python 3.12' },
    ];
    const { element } = render({ tags });
    const items = Array.from(element.querySelectorAll('.resource-card-tags li')).map((li) =>
      li.textContent!.trim(),
    );
    expect(items).toEqual(['GPU Intel', 'ffmpeg 6.1', 'mkvtoolnix 82', '+2']);
  });

  it('emits open from the title and from the footer button', () => {
    const { fixture, element } = render({});
    let opened = 0;
    fixture.componentInstance.open.subscribe(() => opened++);
    (element.querySelector('.resource-card-title') as HTMLButtonElement).click();
    (element.querySelector('.resource-card-foot button') as HTMLButtonElement).click();
    expect(opened).toBe(2);
  });

  it('exposes the meter as a labelled progress bar clamped to 100 %', () => {
    const { element } = render({ meter: { value: 3, max: 2, label: 'Tâches 3 / 2' } });
    const bar = element.querySelector('[role="progressbar"]')!;
    expect(bar.getAttribute('aria-label')).toBe('Tâches 3 / 2');
    expect((bar.firstElementChild as HTMLElement).style.width).toBe('100%');
  });
});
