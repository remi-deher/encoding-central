import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, firstValueFrom, of, switchMap, timer } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import { RecordFormComponent, RecordFormData } from '../../../shared/ui/record-form.component';
import { NotificationService } from '../../../shared/services/notification.service';

interface Collision {
  readonly localUserId: string;
  readonly expectedUsername: string;
  readonly hostUsername: string;
  readonly proposedUsername: string;
}

@Component({
  selector: 'app-join-user-collisions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, ConfigurationSectionComponent, RecordFormComponent],
  template: `
    @if (collisions().length) {
      <app-configuration-section
        icon="server"
        title="Comptes au rattachement"
        description="Le nom du cluster d’accueil est conservé. Confirmez le renommage des comptes entrants ; leurs identités, mots de passe, droits et sessions sont préservés."
      >
        @for (collision of collisions(); track collision.localUserId) {
          <p>
            Le compte entrant « {{ collision.expectedUsername }} » entre en collision avec «
            {{ collision.hostUsername }} » du cluster.
          </p>
          <button
            matButton="outlined"
            type="button"
            [disabled]="busy()"
            (click)="select(collision)"
          >
            Choisir le nom du compte entrant
          </button>
        }
        @if (form(); as data) {
          <app-record-form
            [data]="data"
            [busy]="busy()"
            (submitted)="resolve($event)"
            (cancelled)="form.set(null)"
          />
        }
      </app-configuration-section>
    }
  `,
})
export class JoinUserCollisionsComponent {
  private readonly http = inject(HttpClient);
  private readonly notifications = inject(NotificationService);
  protected readonly collisions = signal<readonly Collision[]>([]);
  protected readonly form = signal<RecordFormData | null>(null);
  protected readonly busy = signal(false);
  private selected: Collision | null = null;

  constructor() {
    timer(0, 5000)
      .pipe(
        switchMap(() =>
          this.http
            .get<readonly Collision[]>('/api/v1/cluster/join-collisions')
            .pipe(catchError(() => of(this.collisions()))),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((collisions) => this.collisions.set(collisions));
  }

  protected select(collision: Collision): void {
    this.selected = collision;
    this.form.set({
      title: 'Renommer le compte entrant',
      description:
        'Le compte du cluster d’accueil conserve son nom. Confirmez le nouveau nom du compte local.',
      submitLabel: 'Confirmer le renommage',
      fields: [{ id: 'username', label: 'Nouveau nom', type: 'text', required: true }],
      initialValues: { username: collision.proposedUsername },
    });
  }

  protected async resolve(values: Record<string, string>): Promise<void> {
    const collision = this.selected;
    if (!collision || this.busy()) return;
    this.busy.set(true);
    try {
      await firstValueFrom(
        this.http.post(`/api/v1/cluster/join-collisions/${collision.localUserId}/resolve`, {
          expectedUsername: collision.expectedUsername,
          username: values['username'],
        }),
      );
      this.collisions.update((items) =>
        items.filter((item) => item.localUserId !== collision.localUserId),
      );
      this.form.set(null);
      this.notifications.show('Compte entrant renommé. Le rattachement peut reprendre.');
    } catch {
      this.notifications.show(
        'Renommage refusé : nom invalide ou réservé, ou compte modifié. Actualisez la proposition.',
      );
    } finally {
      this.busy.set(false);
    }
  }
}
