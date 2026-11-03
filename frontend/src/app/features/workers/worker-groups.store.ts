import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import { NotificationService } from '../../shared/services/notification.service';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { WorkerGroupResponse } from '../../shared/models/central';
import { SaveWorkerGroupPayload } from './components/worker-group-editor.component';

/** Onglet « Groupes de workers » : liste, recherche, édition et fiche détaillée. */
@Injectable()
export class WorkerGroupsStore {
  private readonly http = inject(HttpClient);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);

  readonly groups = signal<readonly WorkerGroupResponse[]>([]);
  readonly groupState = signal<'loading' | 'ready' | 'error'>('loading');
  readonly groupMessage = signal('Chargement des groupes…');
  readonly groupFormOpen = signal(false);
  readonly groupBusy = signal(false);
  readonly groupEditing = signal<WorkerGroupResponse | null>(null);
  readonly groupSearchQuery = signal('');
  readonly selectedGroupForDetail = signal<WorkerGroupResponse | null>(null);

  readonly filteredGroups = computed(() => {
    const query = this.groupSearchQuery().trim().toLowerCase();
    const all = this.groups();
    if (!query) return all;
    return all.filter((g) => {
      const matchName = g.name.toLowerCase().includes(query);
      const matchDesc = g.description?.toLowerCase().includes(query) ?? false;
      const matchLabels = Object.entries(g.labels).some(
        ([k, v]) =>
          k.toLowerCase().includes(query) || (v?.toLowerCase().includes(query) ?? false),
      );
      return matchName || matchDesc || matchLabels;
    });
  });

  createGroup(): void {
    this.groupEditing.set(null);
    this.selectedGroupForDetail.set(null);
    this.groupFormOpen.set(true);
  }

  editGroup(group: WorkerGroupResponse): void {
    this.groupEditing.set(group);
    this.selectedGroupForDetail.set(null);
    this.groupFormOpen.set(true);
  }

  openGroupDetail(group: WorkerGroupResponse): void {
    this.selectedGroupForDetail.set(group);
    this.groupFormOpen.set(false);
  }

  closeGroupForm(): void {
    this.groupFormOpen.set(false);
    this.groupEditing.set(null);
    this.groupBusy.set(false);
  }

  closeGroupDetail(): void {
    this.selectedGroupForDetail.set(null);
  }

  async saveNodeGroup(payload: SaveWorkerGroupPayload): Promise<void> {
    if (this.groupBusy()) return;
    this.groupBusy.set(true);
    const current = this.groupEditing();
    try {
      if (current) {
        await firstValueFrom(this.http.put(`/api/v1/worker-groups/${current.id}`, payload));
      } else {
        await firstValueFrom(this.http.post('/api/v1/worker-groups', payload));
      }
      await this.loadGroups();
      this.closeGroupForm();
      this.notifications.show(
        current ? 'Groupe mis à jour avec succès.' : 'Groupe créé avec succès.',
      );
    } catch {
      this.notifications.show(
        'Le groupe n’a pas pu être enregistré. Vérifiez son nom, ses critères et ses étiquettes.',
      );
    } finally {
      this.groupBusy.set(false);
    }
  }

  confirmRemoveGroup(group: WorkerGroupResponse): void {
    this.dialog
      .open(ConfirmationDialogComponent, {
        data: {
          title: `Supprimer le groupe « ${group.name} » ?`,
          message:
            'Ce groupe et ses étiquettes partagées seront supprimés. Les workers membres et leur inventaire ne sont pas affectés. Attention : un groupe ciblé par un flow ne peut pas être supprimé.',
          confirmLabel: 'Supprimer',
          variant: 'danger',
        },
      })
      .afterClosed()
      .subscribe((confirmed: boolean) => {
        if (confirmed) {
          void this.removeGroup(group);
        }
      });
  }

  private async removeGroup(group: WorkerGroupResponse): Promise<void> {
    try {
      await firstValueFrom(this.http.delete(`/api/v1/worker-groups/${group.id}`));
      await this.loadGroups();
      if (this.selectedGroupForDetail()?.id === group.id) {
        this.closeGroupDetail();
      }
      this.notifications.show('Groupe supprimé avec succès.');
    } catch (error) {
      const title = (error as { error?: { title?: unknown } }).error?.title;
      this.notifications.show(
        typeof title === 'string'
          ? title
          : 'Le groupe n’a pas pu être supprimé. Vérifiez qu’il n’est pas ciblé par un flow.',
      );
    }
  }

  async loadGroups(): Promise<void> {
    this.groupState.set('loading');
    try {
      this.groups.set(
        await firstValueFrom(
          this.http.get<readonly WorkerGroupResponse[]>('/api/v1/worker-groups'),
        ),
      );
      this.groupState.set('ready');
    } catch {
      this.groupState.set('error');
      this.groupMessage.set('Impossible de charger les groupes de workers.');
    }
  }
}
