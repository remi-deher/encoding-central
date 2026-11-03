import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatDialog } from '@angular/material/dialog';
import {
  catchError,
  exhaustMap,
  firstValueFrom,
  forkJoin,
  merge,
  Observable,
  of,
  Subject,
  timeout,
  timer,
} from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import {
  CentralIdentity,
  DiscoveredCentral,
  LocalWorkerPairing,
  LocalWorkerStatus,
  SetupWorkerSheet,
} from '../models/local-worker';
import { ConfirmationDialogComponent } from '../ui/confirmation-dialog.component';
import { CentralApi } from './central-api';
import { NotificationService } from './notification.service';

/** État et opérations du worker local. Le navigateur n'appelle que l'API serveur authentifiée. */
@Injectable()
export class LocalWorkerController {
  private readonly http = inject(HttpClient);
  private readonly api = inject(CentralApi);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly refreshRequested = new Subject<void>();
  private readonly auth = inject(AuthService);
  readonly isAdmin = computed(() => this.auth.isAdmin() || this.auth.setupRequired());
  readonly identity = signal<CentralIdentity | null>(null);
  readonly status = signal<LocalWorkerStatus | null>(null);
  readonly setupNode = signal<SetupWorkerSheet | null>(null);
  readonly clusters = signal<readonly DiscoveredCentral[]>([]);
  readonly loading = signal(true);
  readonly identityUnavailable = signal(false);
  readonly agentUnavailable = signal(false);
  readonly busy = signal(false);
  readonly flow = signal<'join' | null>(null);
  readonly joinIndex = signal(0);
  readonly address = signal('');
  readonly error = signal('');
  readonly target = signal<DiscoveredCentral | null>(null);
  readonly submittedTargetId = signal<string | null>(null);
  readonly pairing = signal<LocalWorkerPairing | null>(null);
  private observedPending = false;
  readonly pending = computed(() => {
    const status = this.status();
    return !!(
      this.submittedTargetId() ||
      (status?.centralId && !status.isEnrolled)
    );
  });
  readonly canJoin = computed(
    () =>
      this.isAdmin() &&
      !this.busy() &&
      !this.agentUnavailable() &&
      this.status() !== null &&
      !this.pending(),
  );
  readonly joinableClusters = computed(() => {
    const localClusterId = this.status()?.centralId?.toLowerCase();
    const localIdentityId = this.identity()?.id?.toLowerCase();
    return this.clusters().filter((cluster) => {
      if (cluster.source === 'configured') return false;
      const id = cluster.id.toLowerCase();
      if (localClusterId && id === localClusterId) return false;
      if (localIdentityId && id === localIdentityId) return false;
      try {
        const host = new URL(cluster.apiBaseUrl).hostname.toLowerCase();
        if (host === 'localhost' || host === '127.0.0.1' || host === '::1') return false;
      } catch {}
      return true;
    });
  });
  readonly targetName = computed(
    () => this.target()?.name ?? this.status()?.centralName ?? 'le central d’accueil',
  );

  constructor() {
    merge(timer(0, 5_000), this.refreshRequested)
      .pipe(
        exhaustMap(() =>
          this.auth.setupRequired()
            ? forkJoin({
                identity: this.api.identity().pipe(
                  timeout(8_000),
                  catchError(() => of(null)),
                ),
                setupNode: this.http.get<SetupWorkerSheet>('/api/v1/auth/setup/worker').pipe(
                  timeout(8_000),
                  catchError(() => of(null)),
                ),
                agent: of(null),
              })
            : forkJoin({
                identity: this.api.identity().pipe(
                  timeout(8_000),
                  catchError(() => of(null)),
                ),
                setupNode: of(null),
                agent: forkJoin({
                  status: this.http.get<LocalWorkerStatus>('/api/v1/local-worker/status'),
                  clusters: this.http.get<readonly DiscoveredCentral[]>('/api/v1/local-worker/centrals'),
                }).pipe(
                  timeout(8_000),
                  catchError(() => of(null)),
                ),
              }),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ identity, setupNode, agent }) => {
        this.identityUnavailable.set(identity === null);
        this.agentUnavailable.set(
          this.auth.setupRequired() ? !setupNode?.agentAvailable : agent === null,
        );
        if (identity) this.identity.set(identity);
        if (setupNode) {
          this.setupNode.set(setupNode);
          this.status.set({
            workerId: '',
            name: setupNode.name ?? 'localhost',
            displayName: setupNode.displayName,
            roles: [],
            maxConcurrentTasks: 0,
            automaticEnrollmentEnabled: false,
            capabilities: (setupNode.tools ?? []).map((tool) => ({
              toolId: tool.toolId,
              version: '',
              mediaTypes: [],
              accelerationTypes: tool.accelerationTypes,
              encoderChecks: tool.encoderChecks,
            })),
            inventory: setupNode.inventory,
            isEnrolled: false,
            pairingKey: null,
            pairingExpiresAt: null,
            centralId: null,
            centralName: null,
            verificationCode: null,
            enrollmentFailure: null,
          });
        }
        if (agent) {
          this.clusters.set(agent.clusters);
          this.observeStatus(agent.status);
          this.status.set(agent.status);
          if (agent.status?.pairingKey && !this.pairing()) {
            this.pairing.set({
              pairingKey: agent.status.pairingKey,
              expiresAt: agent.status.pairingExpiresAt ?? '',
              verificationCode: agent.status.verificationCode,
            });
          } else if (
            agent.status?.verificationCode &&
            this.pairing() &&
            !this.pairing()?.verificationCode
          ) {
            this.pairing.update((p) =>
              p ? { ...p, verificationCode: agent.status.verificationCode } : p,
            );
          }
        }
        this.loading.set(false);
      });
  }

  refresh(): void {
    this.refreshRequested.next();
  }

  openJoin(): void {
    if (!this.isAdmin() || this.busy()) return;
    this.error.set('');
    if (!this.pending()) this.target.set(null);
    this.joinIndex.set(this.pending() ? 2 : 0);
    this.flow.set('join');
  }

  closeFlow(): void {
    if (!this.busy()) this.flow.set(null);
  }

  async verifyAddress(values: Record<string, string>): Promise<void> {
    if (!this.canJoin()) return;
    const address = values['address']?.trim() ?? '';
    this.address.set(address);
    try {
      const url = new URL(address);
      if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password)
        throw new Error();
    } catch {
      this.error.set('Saisissez une adresse HTTP ou HTTPS complète, sans identifiants.');
      return;
    }
    await this.run(
      this.http.post<DiscoveredCentral>('/api/v1/local-worker/centrals', { apiBaseUrl: address }),
      (cluster) => {
        // Seul le rattachement déjà établi de l'agent compte : l'identité relayée par l'interface est celle du central lui-même.
        const localClusterId = this.status()?.centralId?.toLowerCase();
        if (
          (localClusterId && cluster.id.toLowerCase() === localClusterId) ||
          cluster.source === 'configured'
        ) {
          this.error.set(
            'Cette adresse correspond au central actuel de ce worker. Choisissez un autre central.',
          );
          return;
        }
        try {
          const host = new URL(cluster.apiBaseUrl).hostname.toLowerCase();
          if (host === 'localhost' || host === '127.0.0.1' || host === '::1') {
            this.error.set(
              'Cette adresse correspond au central local de cette machine. Choisissez un autre central distant.',
            );
            return;
          }
        } catch {}
        this.target.set(cluster);
        this.joinIndex.set(1);
        this.refresh();
      },
      'La vérification du central a échoué. Vérifiez son adresse et son port, puis réessayez. Aucune demande n’a été envoyée.',
    );
  }

  chooseCluster(id: string): void {
    const cluster = this.joinableClusters().find((item) => item.id === id);
    if (cluster) void this.verifyAddress({ address: cluster.apiBaseUrl });
  }

  async sendRequest(displayName?: string): Promise<void> {
    const target = this.target();
    if (!target || !this.canJoin()) return;
    const effectiveDisplayName =
      displayName?.trim() || this.status()?.displayName?.trim() || undefined;
    await this.run(
      this.http.post<LocalWorkerPairing>('/api/v1/local-worker/pairing-key', {
        clusterId: target.id,
        displayName: effectiveDisplayName,
      }),
      (pairing) => {
        this.pairing.set(pairing);
        this.submittedTargetId.set(target.id);
        this.observedPending = false;
        this.joinIndex.set(2);
        this.notifications.show(`Demande envoyée à ${target.name}.`);
        this.refresh();
      },
      'L’envoi de la demande a échoué. Vous pouvez réessayer sans ressaisir l’adresse.',
    );
  }

  /** Rattachement direct : le compte administrateur du central, dont la session est relayée à l'agent, admet ce worker (ADR 0034). */
  async enroll(displayName?: string): Promise<void> {
    const target = this.target();
    if (!target || !this.canJoin()) return;
    await this.run(
      this.http.post<unknown>('/api/v1/local-worker/enroll', {
        clusterId: target.id,
        displayName: displayName?.trim() || this.status()?.displayName?.trim() || undefined,
      }),
      () => {
        this.submittedTargetId.set(target.id);
        this.observedPending = false;
        this.joinIndex.set(3);
        this.notifications.show(`Ce worker est rattaché à ${target.name}.`);
        this.refresh();
      },
      'Le rattachement a échoué. Vérifiez que le compte est administrateur du central, puis réessayez.',
    );
  }

  async leave(): Promise<void> {
    if (!this.isAdmin() || this.busy() || this.agentUnavailable() || !this.status()?.centralId)
      return;
    const pending = this.pending();
    const confirmed = await firstValueFrom(
      this.dialog
        .open(ConfirmationDialogComponent, {
          data: {
            title: pending ? 'Annuler cette demande ?' : 'Quitter ce central ?',
            message: pending
              ? 'La demande de rattachement sera abandonnée.'
              : 'Ce worker n’est plus rattaché à aucun central. Ses bibliothèques locales et son historique restent intacts. Un traitement actif peut empêcher le départ.',
            confirmLabel: pending ? 'Annuler la demande' : 'Quitter le central',
          },
        })
        .afterClosed()
        .pipe(takeUntilDestroyed(this.destroyRef)),
    );
    if (!confirmed) return;
    if (pending !== this.pending()) {
      this.notifications.show(
        'L’état du rattachement a changé pendant la confirmation. Consultez le nouvel état avant de continuer.',
      );
      return;
    }
    await this.run(
      this.http.post<void>('/api/v1/local-worker/leave', {}),
      () => {
        this.submittedTargetId.set(null);
        this.pairing.set(null);
        this.observedPending = false;
        this.flow.set(null);
        this.target.set(null);
        this.joinIndex.set(0);
        this.notifications.show(
          pending ? 'Demande annulée.' : 'Départ demandé. L’état sera confirmé par le worker.',
        );
        this.refresh();
      },
      pending ? 'Impossible d’annuler la demande.' : 'Le worker n’a pas pu quitter ce central.',
    );
  }

  async forgetCluster(id: string): Promise<void> {
    const cluster = this.joinableClusters().find(
      (item) => item.id === id && item.source === 'manual',
    );
    if (!cluster) return;
    await this.run(
      this.http.delete<void>(`/api/v1/local-worker/centrals/${cluster.id}`),
      () => {
        this.clusters.update((clusters) => clusters.filter((item) => item.id !== id));
        this.notifications.show(`L’adresse de ${cluster.name} est oubliée.`);
      },
      'Impossible d’oublier cette adresse.',
    );
  }

  back(): void {
    if (!this.busy() && !this.pending()) {
      this.error.set('');
      this.joinIndex.set(0);
    }
  }

  private observeStatus(next: LocalWorkerStatus): void {
    let targetId = this.submittedTargetId();
    if (!targetId && next.centralId && !next.isEnrolled) {
      targetId = next.centralId;
      this.submittedTargetId.set(targetId);
      this.observedPending = true;
      if (!this.target())
        this.target.set(
          this.clusters().find((cluster) => cluster.id.toLowerCase() === targetId!.toLowerCase()) ??
            (next.centralId
              ? {
                  id: next.centralId,
                  name: next.centralName ?? 'le central d’accueil',
                  apiBaseUrl: '',
                  discoveredAt: '',
                }
              : null),
        );
    }
    if (!targetId) return;
    const sameTarget = next.centralId?.toLowerCase() === targetId.toLowerCase();
    if (sameTarget && next.isEnrolled) {
      this.submittedTargetId.set(null);
      this.pairing.set(null);
      this.observedPending = false;
      this.joinIndex.set(3);
      this.notifications.show(`Ce worker a rejoint ${next.centralName ?? this.targetName()}.`);
    } else if (sameTarget && !next.isEnrolled) {
      this.observedPending = true;
    } else if (this.observedPending) {
      this.submittedTargetId.set(null);
      this.pairing.set(null);
      this.observedPending = false;
      this.joinIndex.set(0);
      this.error.set(
        next.enrollmentFailure
          ? `Rattachement annulé : ${next.enrollmentFailure}`
          : 'La demande a expiré ou a été annulée. Le worker a conservé son état précédent.',
      );
      this.notifications.show(this.error());
    }
  }

  private async run<T>(
    request: Observable<T>,
    success: (value: T) => void,
    fallback: string,
  ): Promise<void> {
    if (!this.isAdmin() || this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      const value = await firstValueFrom(
        request.pipe(timeout(30_000), takeUntilDestroyed(this.destroyRef)),
      );
      if (!this.destroyRef.destroyed) success(value);
    } catch (error) {
      if (!this.destroyRef.destroyed) {
        const detail = error instanceof HttpErrorResponse ? error.error?.title : null;
        this.error.set(typeof detail === 'string' ? `${fallback} ${detail}` : fallback);
        this.notifications.show(this.error());
      }
    } finally {
      if (!this.destroyRef.destroyed) this.busy.set(false);
    }
  }
}
