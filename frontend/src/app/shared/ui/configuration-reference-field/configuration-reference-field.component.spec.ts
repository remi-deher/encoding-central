import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { ConfigurationReferenceFieldComponent } from './configuration-reference-field.component';

describe('Configuration references', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }),
  );
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('offers only observed font sets and retains a missing saved set', async () => {
    const fixture = TestBed.createComponent(ConfigurationReferenceFieldComponent);
    fixture.componentRef.setInput('field', {
      id: 'fontSet',
      type: 'text',
      label: 'Jeu de polices',
      editor: 'font-set',
    });
    fixture.componentRef.setInput('value', 'Saved but unavailable');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/v1/nodes')
      .flush([
        {
          id: 'worker',
          name: 'Worker',
          roles: ['worker'],
          status: 'online',
          inventory: { fontSets: ['Anime'] },
        },
      ]);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('input').value).toBe('Saved but unavailable');
    const input = fixture.nativeElement.querySelector('input');
    input.value = 'Anime';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    input.dispatchEvent(new Event('focusin'));
    await fixture.whenStable();
    expect(document.body.textContent).toContain('Anime');
  });
  it('displays the observed worker name while retaining its stored identity', async () => {
    const fixture = TestBed.createComponent(ConfigurationReferenceFieldComponent);
    fixture.componentRef.setInput('field', {
      id: 'worker',
      type: 'text',
      label: 'Worker',
      editor: 'node',
    });
    fixture.componentRef.setInput('value', 'worker-id');
    const changed = vi.fn();
    fixture.componentInstance.valueChange.subscribe(changed);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/v1/nodes')
      .flush([
        {
          id: 'worker-id',
          name: 'Worker',
          displayName: 'Encodeur salon',
          roles: ['worker'],
          status: 'online',
        },
      ]);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('input').value).toBe('Encodeur salon · online');
    expect(changed).not.toHaveBeenCalled();
  });
  it('preserves an unavailable worker without silently selecting another', async () => {
    const fixture = TestBed.createComponent(ConfigurationReferenceFieldComponent);
    fixture.componentRef.setInput('field', {
      id: 'targetNodeId',
      type: 'text',
      label: 'Worker',
      editor: 'node',
    });
    fixture.componentRef.setInput('value', 'missing-worker');
    const changed = vi.fn();
    fixture.componentInstance.valueChange.subscribe(changed);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/nodes').flush([]);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('input').value).toBe('missing-worker');
    expect(changed).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain('Aucune ressource observée');
  });
  it('adds languages without replacing existing historical or unknown codes', async () => {
    const fixture = TestBed.createComponent(ConfigurationReferenceFieldComponent);
    fixture.componentRef.setInput('field', {
      id: 'languages',
      type: 'text',
      label: 'Langues',
      editor: 'languages',
    });
    fixture.componentRef.setInput('value', 'fre,xyz');
    const changed = vi.fn();
    fixture.componentInstance.valueChange.subscribe(changed);
    await fixture.whenStable();
    const input = fixture.nativeElement.querySelector('input');
    input.value = 'jpn';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    expect(changed).toHaveBeenCalledWith('fre,xyz,jpn');
  });
});
