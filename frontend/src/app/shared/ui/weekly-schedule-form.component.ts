import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  input,
  output,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import {
  NodeSchedule,
  SCHEDULE_DAYS,
  ScheduleInterval,
  allSlots,
  isValidSlots,
  paintDay,
} from '../models/node-schedule';
import { WeeklyScheduleGridComponent } from './weekly-schedule-grid.component';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-weekly-schedule-form',
  imports: [
    MatButtonModule,
    MatButtonToggleModule,
    MatSlideToggleModule,
    WeeklyScheduleGridComponent,
  ],
  template: `
    <section class="schedule-form" aria-label="Calendrier hebdomadaire">
      <mat-slide-toggle [checked]="enabled()" (change)="enabled.set($event.checked)">
        Activer le calendrier
      </mat-slide-toggle>
      <p class="schedule-hint">
        Hors des plages colorées, aucun nouveau traitement ne démarre sur ce nœud. Les traitements
        déjà en cours ne sont pas interrompus. Fuseau horaire : {{ timeZone() }}.
      </p>
      @if (enabled()) {
        <div class="schedule-tools">
          <mat-button-toggle-group
            aria-label="Précision de la grille"
            [value]="interval()"
            (change)="interval.set($event.value)"
          >
            <mat-button-toggle [value]="15">15 min</mat-button-toggle>
            <mat-button-toggle [value]="30">30 min</mat-button-toggle>
            <mat-button-toggle [value]="60">1 h</mat-button-toggle>
          </mat-button-toggle-group>
          <button matButton="text" type="button" (click)="slots.set(allOpen())">
            Tout autoriser
          </button>
          <button matButton="text" type="button" (click)="slots.set(allClosed())">
            Tout bloquer
          </button>
        </div>
        <app-weekly-schedule-grid
          [slots]="slots()"
          [interval]="interval()"
          [disabled]="busy()"
          (slotsChange)="slots.set($event)"
        />
        <div class="schedule-days" role="group" aria-label="Autoriser ou bloquer un jour entier">
          @for (day of days; track day; let index = $index) {
            <button matButton="text" type="button" (click)="toggleDay(index)">{{ day }}</button>
          }
        </div>
        @if (!valid()) {
          <p class="schedule-error" role="alert">Autorisez au moins un créneau.</p>
        }
      }
      <div class="form-actions">
        <button matButton="text" type="button" [disabled]="busy()" (click)="cancelled.emit()">
          Annuler
        </button>
        <button matButton="filled" type="button" [disabled]="busy() || !valid()" (click)="save()">
          Enregistrer
        </button>
      </div>
    </section>
  `,
  styles: `
    .schedule-form {
      display: grid;
      gap: 14px;
    }
    .schedule-hint {
      margin: 0;
      color: var(--text-secondary);
      font-size: var(--font-body);
      line-height: 1.5;
    }
    .schedule-tools,
    .schedule-days,
    .form-actions {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-2);
    }
    .form-actions {
      justify-content: flex-end;
    }
    .schedule-error {
      margin: 0;
      color: var(--danger, #b3261e);
      font-size: var(--font-body);
    }
  `,
})
export class WeeklyScheduleFormComponent implements OnInit {
  readonly schedule = input.required<NodeSchedule>();
  readonly busy = input(false);
  readonly saved = output<NodeSchedule>();
  readonly cancelled = output<void>();

  protected readonly days = SCHEDULE_DAYS;
  protected readonly enabled = signal(false);
  protected readonly slots = signal(allSlots(true));
  protected readonly interval = signal<ScheduleInterval>(60);
  protected readonly timeZone = signal('UTC');
  protected readonly valid = computed(() => !this.enabled() || this.slots().includes('1'));

  ngOnInit(): void {
    const current = this.schedule();
    this.enabled.set(current.enabled);
    this.slots.set(isValidSlots(current.slots) ? current.slots : allSlots(true));
    // Le fuseau enregistré prime ; à la première activation, le navigateur de l’administrateur fait foi.
    const browser = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
    this.timeZone.set(current.enabled ? current.timeZone : browser);
  }

  protected allOpen(): string {
    return allSlots(true);
  }

  protected allClosed(): string {
    return allSlots(false);
  }

  protected toggleDay(day: number): void {
    const perDay = this.slots().length / 7;
    const open = this.slots()
      .slice(day * perDay, (day + 1) * perDay)
      .includes('0');
    this.slots.set(paintDay(this.slots(), day, open));
  }

  protected save(): void {
    this.saved.emit({ enabled: this.enabled(), timeZone: this.timeZone(), slots: this.slots() });
  }
}
