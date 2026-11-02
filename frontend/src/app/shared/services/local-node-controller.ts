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
  ClusterIdentity,
  DiscoveredCluster,
  LocalNodePairing,
  LocalNodeStatus,
  SetupNodeSheet,
} from '../models/local-node';
import { NodeMode, NodeModeStatus } from '../models/node-mode';
import { ConfirmationDialogComponent } from '../ui/confirmation-dialog.component';
import { ClusterApi } from './cluster-api';
import { NodeModeController } from './node-mode-controller';
import { NotificationService } from './notification.service';

/** État et opérations du nœud local. Le navigateur n'appelle que l'API serveur authentifiée. */
@Injectable()
export class LocalNodeController {
  private readonly http = inject(HttpClient);
  private readonly api = inject(ClusterApi);
  private readonly nodeMode = inject(NodeModeController);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly refreshRequested = new Subject<void>();
  private readonly auth = inject(AuthService);
  readonly isAdmin = computed(() => this.auth.isAdmin() || this.auth.setupRequired());
  readonly identity = signal<ClusterIdentity | null>(null);
  readonly status = signal<LocalNodeStatus | null>(null);
  readonly setupNode = signal<SetupNodeSheet | null>(null);
  readonly mode = signal<NodeModeStatus | null>(null);
  readonly clusters = signal<readonly DiscoveredCluster[]>([]);
  readonly loading = signal(true);
  readonly identityUnavailable = signal(false);
  readonly agentUnavailable = signal(false);
  readonly modeUnavailable = signal(false);
  readonly busy = signal(false);
  readonly modeBusy = signal(false);
  readonly flow = signal<'create' | 'join' | null>(null);
  readonly joinIndex = signal(0);
  readonly address = signal('');
  readonly error = signal('');
  readonly target = signal<DiscoveredCluster | null>(null);
  readonly submittedTargetId = signal<string | null>(null);
  readonly pairing = signal<LocalNodePairing | null>(null);
  private observedPending = false;
  readonly externalCluster = computed(() => {
    const status = this.status();
    return !!(
      status?.isEnrolled &&
      !status.switching &&
      status.clusterId &&
      this.identity() &&
      status.clusterId.toLowerCase() !== this.identity()!.id.toLowerCase()
    );
  });
  readonly standalone = computed(
    () => !this.externalCluster() && this.identity()?.standalone === true,
  );
  readonly pending = computed(() => {
    const status = this.status();
    return !!(
      this.submittedTargetId() ||
      status?.switching ||
      (status?.clusterId && !status.isEnrolled)
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
    const localClusterId = this.status()?.clusterId?.toLowerCase();
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
    () => this.target()?.name ?? this.status()?.clusterName ?? 'le cluster d’accueil',
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
                setupNode: this.http.get<SetupNodeSheet>('/api/v1/auth/setup/node').pipe(
                  timeout(8_000),
                  catchError(() => of(null)),
                ),
                mode: of(null),
                agent: of(null),
              })
            : forkJoin({
                identity: this.api.identity().pipe(
                  timeout(8_000),
                  catchError(() => of(null)),
                ),
                setupNode: of(null),
                mode: this.http.get<NodeModeStatus>('/api/v1/node/mode').pipe(
                  timeout(8_000),
                  catchError(() => of(null)),
                ),
                agent: forkJoin({
                  status: this.http.get<LocalNodeStatus>('/api/v1/local-node/status'),
                  clusters: this.http.get<readonly DiscoveredCluster[]>('/api/v1/local-node/clusters'),
                }).pipe(
                  timeout(8_000),
                  catchError(() => of(null)),
                ),
              }),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ identity, setupNode, mode, agent }) => {
        this.identityUnavailable.set(identity === null);
        this.modeUnavailable.set(!this.auth.setupRequired() && mode === null);
        this.agentUnavailable.set(
          this.auth.setupRequired() ? !setupNode?.agentAvailable : agent === null,
        );
        if (identity) this.identity.set(identity);
        if (setupNode) {
          this.setupNode.set(setupNode);
          this.status.set({
            nodeId: '',
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
            clusterId: null,
            clusterName: null,
            verificationCode: null,
            switching: null,
            switchFailure: null,
          });
        }
        if (mode) this.mode.set(mode);
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

  openCreate(): void {
    if (!this.isAdmin() || !this.standalone() || this.identityUnavailable() || this.busy()) return;
    this.error.set('');
    this.flow.set('create');
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

  async setRole(role: 'server' | 'worker', enabled: boolean): Promise<void> {
    if (!this.isAdmin() || this.busy() || this.modeBusy()) return;
    this.modeBusy.set(true);
    try {
      const status = await firstValueFrom(this.http.get<NodeModeStatus>('/api/v1/node/mode'));
      if (!status.managed) {
        this.notifications.show(
          'Les fonctions de ce nœud sont définies par la configuration du déploiement.',
        );
        return;
      }
      const target: NodeMode = {
        server: role === 'server' ? enabled : status.server,
        worker: role === 'worker' ? enabled : status.worker,
      };
      if (!target.server && !target.worker) {
        this.notifications.show(
          'Gardez au moins une fonction : sans serveur ni worker, le nœud ne ferait plus partie du cluster.',
        );
        return;
      }
      if (!target[role] && !(await this.nodeMode.confirmRoleDeactivation(role))) return;
      if (await this.nodeMode.apply('/api/v1/node/mode', target)) this.refresh();
    } catch {
      this.notifications.show('Impossible de lire le mode de ce nœud.');
    } finally {
      this.modeBusy.set(false);
    }
  }

  async create(values: Record<string, string>): Promise<void> {
    const name = values['name']?.trim();
    if (!name || name.length > 128) {
      this.error.set('Le nom du cluster est obligatoire et limité à 128 caractères.');
      return;
    }
    if (!this.standalone() || this.identityUnavailable()) return;
    await this.run(
      this.http.post<ClusterIdentity>('/api/v1/cluster', { name }),
      (identity) => {
        this.identity.set(identity);
        this.flow.set(null);
        this.notifications.show(
          `Le cluster ${identity.name} est créé. Ce nœud en est le premier membre.`,
        );
        this.refresh();
      },
      'La création du cluster a échoué. Votre saisie est conservée.',
    );
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
      this.http.post<DiscoveredCluster>('/api/v1/local-node/clusters', { apiBaseUrl: address }),
      (cluster) => {
        const localClusterId = this.status()?.clusterId?.toLowerCase();
        const localIdentityId = this.identity()?.id?.toLowerCase();
        if (
          (localClusterId && cluster.id.toLowerCase() === localClusterId) ||
          (localIdentityId && cluster.id.toLowerCase() === localIdentityId) ||
          cluster.source === 'configured'
        ) {
          this.error.set(
            'Cette adresse correspond au cluster actuel de ce nœud. Choisissez un autre cluster.',
          );
          return;
        }
        try {
          const host = new URL(cluster.apiBaseUrl).hostname.toLowerCase();
          if (host === 'localhost' || host === '127.0.0.1' || host === '::1') {
            this.error.set(
              'Cette adresse correspond au cluster local de cette machine. Choisissez un autre cluster distant.',
            );
            return;
          }
        } catch {}
        this.target.set(cluster);
        this.joinIndex.set(1);
        this.refresh();
      },
      'La vérification du cluster a échoué. Vérifiez son adresse et son port, puis réessayez. Aucune demande n’a été envoyée.',
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
      this.http.post<LocalNodePairing>('/api/v1/local-node/pairing-key', {
        clusterId: target.id,
        leaveCurrent: !!this.status()?.clusterId,
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

  async leave(): Promise<void> {
    if (!this.isAdmin() || this.busy() || this.agentUnavailable() || !this.status()?.clusterId)
      return;
    const pending = this.pending();
    const confirmed = await firstValueFrom(
      this.dialog
        .open(ConfirmationDialogComponent, {
          data: {
            title: pending ? 'Annuler cette demande ?' : 'Quitter ce cluster ?',
            message: pending
              ? 'La demande sera abandonnée. Le rattachement précédent est conservé s’il existe.'
              : 'Ce nœud redevient autonome. Ses bibliothèques locales et son historique restent intacts. Un traitement actif peut empêcher le départ.',
            confirmLabel: pending ? 'Annuler la demande' : 'Quitter le cluster',
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
      this.http.post<void>('/api/v1/local-node/leave', {}),
      () => {
        this.submittedTargetId.set(null);
        this.pairing.set(null);
        this.observedPending = false;
        this.flow.set(null);
        this.target.set(null);
        this.joinIndex.set(0);
        this.notifications.show(
          pending ? 'Demande annulée.' : 'Départ demandé. L’état sera confirmé par le nœud.',
        );
        this.refresh();
      },
      pending ? 'Impossible d’annuler la demande.' : 'Le nœud n’a pas pu quitter ce cluster.',
    );
  }

  async forgetCluster(id: string): Promise<void> {
    const cluster = this.joinableClusters().find(
      (item) => item.id === id && item.source === 'manual',
    );
    if (!cluster) return;
    await this.run(
      this.http.delete<void>(`/api/v1/local-node/clusters/${cluster.id}`),
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

  private observeStatus(next: LocalNodeStatus): void {
    let targetId = this.submittedTargetId();
    if (!targetId && next.clusterId && (!next.isEnrolled || next.switching)) {
      targetId = next.clusterId;
      this.submittedTargetId.set(targetId);
      this.observedPending = true;
      if (!this.target())
        this.target.set(
          this.clusters().find((cluster) => cluster.id.toLowerCase() === targetId!.toLowerCase()) ??
            (next.clusterId
              ? {
                  id: next.clusterId,
                  name: next.clusterName ?? 'le cluster d’accueil',
                  apiBaseUrl: '',
                  discoveredAt: '',
                }
              : null),
        );
    }
    if (!targetId) return;
    const sameTarget = next.clusterId?.toLowerCase() === targetId.toLowerCase();
    if (sameTarget && next.isEnrolled && !next.switching) {
      this.submittedTargetId.set(null);
      this.pairing.set(null);
      this.observedPending = false;
      this.joinIndex.set(3);
      this.notifications.show(`Ce nœud a rejoint ${next.clusterName ?? this.targetName()}.`);
    } else if (sameTarget && (!next.isEnrolled || next.switching)) {
      this.observedPending = true;
    } else if (this.observedPending) {
      this.submittedTargetId.set(null);
      this.pairing.set(null);
      this.observedPending = false;
      this.joinIndex.set(0);
      this.error.set(
        next.switchFailure
          ? `Rattachement annulé : ${next.switchFailure}`
          : 'La demande a expiré ou a été annulée. Le nœud a conservé son état précédent.',
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
