import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import { NodeMode, NodeModeStatus } from '../models/node-mode';
import { ConfirmationDialogComponent } from '../ui/confirmation-dialog.component';
import { NotificationService } from './notification.service';

/**
 * Parcours commun de changement du mode du nœud local (serveur et console du worker) : lecture, choix, confirmation
 * quand le serveur local va s'arrêter, application puis retour à l'utilisateur. L'adresse seule distingue les deux interfaces.
 */
@Injectable({ providedIn: 'root' })
export class NodeModeController {
  private readonly http = inject(HttpClient);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);

  /** Modifie directement une fonction à la suite d'un switch dans la fiche du nœud. */
  async requestRole(
    nodeId: string,
    nodeName: string,
    current: NodeMode,
    role: 'server' | 'worker',
    enabled: boolean,
    online: boolean,
  ): Promise<boolean> {
    const mode: NodeMode = { ...current, [role]: enabled };
    if (!mode.server && !mode.worker) {
      this.notifications.show('Le nœud doit garder au moins une fonction : serveur ou worker.');
      return false;
    }
    if (!enabled && !(await this.confirmRoleDeactivation(role, nodeName))) return false;
    try {
      await firstValueFrom(this.http.put(`/api/v1/nodes/${nodeId}/mode`, mode));
      this.notifications.show(
        online
          ? 'Changement demandé : le nœud l’applique à son prochain signal.'
          : 'Changement enregistré : le nœud l’appliquera à son retour en ligne.',
      );
      return true;
    } catch (error) {
      this.notifications.show(this.errorMessage(error));
      return false;
    }
  }

  async apply(endpoint: string, mode: NodeMode): Promise<NodeModeStatus | null> {
    try {
      const applied = await firstValueFrom(this.http.put<NodeModeStatus>(endpoint, mode));
      this.notifications.show(
        'Mode appliqué. Les fonctions retirées s’arrêtent dans quelques secondes.',
      );
      return applied;
    } catch (error) {
      this.notifications.show(this.errorMessage(error));
      return null;
    }
  }

  confirmRoleDeactivation(role: 'server' | 'worker', nodeName?: string): Promise<boolean> {
    const subject = nodeName ? ` de « ${nodeName} »` : ' de ce nœud';
    const server = role === 'server';
    return firstValueFrom(
      this.dialog
        .open<ConfirmationDialogComponent, unknown, boolean>(ConfirmationDialogComponent, {
          data: {
            compact: true,
            variant: 'danger',
            eyebrow: 'Désactivation',
            title: `Désactiver la fonction ${server ? 'serveur' : 'worker'}${subject} ?`,
            message: server
              ? 'L’interface et l’API de ce nœud s’arrêteront. Les workers pourront le retrouver s’il revient ou utiliser un autre serveur du cluster.'
              : 'Ce nœud ne recevra plus de nouvelles tâches de traitement après l’application du changement.',
            confirmLabel: `Désactiver ${server ? 'serveur' : 'worker'}`,
          },
        })
        .afterClosed(),
    ).then((confirmed) => confirmed === true);
  }

  private errorMessage(error: unknown): string {
    const title = (error as { error?: { title?: string } } | null)?.error?.title;
    return title ?? 'Le changement de mode a échoué.';
  }
}
