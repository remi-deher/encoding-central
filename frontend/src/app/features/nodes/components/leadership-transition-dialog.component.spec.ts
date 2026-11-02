import { TestBed } from '@angular/core/testing';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import {
  LeadershipTransitionDialogComponent,
  LeadershipTransitionDialogData,
} from './leadership-transition-dialog.component';
import { ClusterApi } from '../../../shared/services/cluster-api';

function create(
  data: LeadershipTransitionDialogData,
  transferResult?: { success: boolean; message: string; newMasterUrl: string | null } | Error,
) {
  const close = vi.fn();
  const transferLeadership = vi.fn().mockImplementation(() => {
    if (transferResult instanceof Error) {
      return throwError(() => transferResult);
    }
    return of(
      transferResult ?? { success: true, message: 'OK', newMasterUrl: 'http://192.168.1.51:8090/' },
    );
  });

  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: { close, addPanelClass: vi.fn() } },
      { provide: ClusterApi, useValue: { transferLeadership } },
    ],
  });

  const fixture = TestBed.createComponent(LeadershipTransitionDialogComponent);
  fixture.detectChanges();
  return { fixture, close, transferLeadership, element: fixture.nativeElement as HTMLElement };
}

describe('LeadershipTransitionDialogComponent', () => {
  const sampleData: LeadershipTransitionDialogData = {
    targetNodeId: '22222222-2222-2222-2222-222222222222',
    targetNodeName: 'encoding-51',
    currentNodeName: 'encoding-50',
    targetNodeHost: '192.168.1.51',
    animationDelayMs: 0,
  };

  it('renders confirmation state with target node name and input URL', () => {
    const { element } = create(sampleData);
    expect(element.textContent).toContain('encoding-51');
    expect(element.textContent).toContain('encoding-50');
    expect(element.textContent).toContain('Transférer la gouvernance');

    const input = element.querySelector('input.url-input') as HTMLInputElement;
    expect(input).not.toBeNull();
    expect(input.value).toBe('http://192.168.1.51:8090/');
  });

  it('closes with false when clicking Annuler', () => {
    const { element, close } = create(sampleData);
    const cancelBtn = Array.from(element.querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Annuler'),
    );
    expect(cancelBtn).toBeDefined();
    cancelBtn?.click();
    expect(close).toHaveBeenCalledWith(false);
  });

  it('displays timeline steps when transfer is initiated', async () => {
    const { fixture, element, transferLeadership } = create(sampleData);
    const startBtn = Array.from(element.querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Transférer la gouvernance'),
    ) as HTMLButtonElement;

    expect(startBtn).toBeDefined();
    startBtn.click();
    fixture.detectChanges();

    expect(transferLeadership).toHaveBeenCalledWith(
      '22222222-2222-2222-2222-222222222222',
      'http://192.168.1.51:8090/',
    );

    expect(element.textContent).toContain('Préparation & Drainage');
    expect(element.textContent).toContain('Instantané SQLite');
    expect(element.textContent).toContain('Bascule des rôles');
  });

  it('shows error state when transfer request fails', async () => {
    const { fixture, element } = create(sampleData, new Error('Nœud cible injoignable.'));
    const startBtn = Array.from(element.querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Transférer la gouvernance'),
    ) as HTMLButtonElement;

    startBtn.click();
    fixture.detectChanges();

    await fixture.whenStable();
    fixture.detectChanges();

    expect(element.textContent).toContain('Nœud cible injoignable.');
    expect(element.textContent).toContain('Réessayer');
  });
});
