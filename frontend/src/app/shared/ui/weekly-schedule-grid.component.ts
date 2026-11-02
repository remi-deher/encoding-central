import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  CellState,
  SCHEDULE_DAYS,
  ScheduleInterval,
  cellState,
  cellsPerDay,
  paintCells,
} from '../models/node-schedule';

/** Grille jour × heure : cliquer ou glisser autorise ou bloque les créneaux. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-weekly-schedule-grid',
  template: `
    <div class="schedule-scroll">
      <div
        class="schedule-grid"
        role="group"
        [attr.aria-label]="label()"
        [style.--cells]="cells().length"
        (pointermove)="onPointerMove($event)"
        (pointerup)="endPaint()"
        (pointerleave)="endPaint()"
        (pointercancel)="endPaint()"
      >
        <span class="corner"></span>
        @for (hour of hours(); track hour.index) {
          <span class="hour" [style.grid-column]="hour.column">{{ hour.label }}</span>
        }
        @for (day of days; track day; let dayIndex = $index) {
          <span class="day">{{ day }}</span>
          @for (cell of cells(); track cell) {
            <button
              type="button"
              class="cell"
              [class.cell_on]="state(dayIndex, cell) === 'on'"
              [class.cell_partial]="state(dayIndex, cell) === 'partial'"
              [attr.data-day]="dayIndex"
              [attr.data-cell]="cell"
              [attr.aria-pressed]="state(dayIndex, cell) === 'on'"
              [attr.aria-label]="cellLabel(dayIndex, cell)"
              [disabled]="disabled()"
              (pointerdown)="startPaint($event, dayIndex, cell)"
              (keydown.enter)="toggleCell(dayIndex, cell)"
              (keydown.space)="toggleCell(dayIndex, cell); $event.preventDefault()"
            ></button>
          }
        }
      </div>
    </div>
  `,
  styles: `
    .schedule-scroll {
      overflow-x: auto;
      padding-bottom: var(--space-1);
    }
    .schedule-grid {
      --cell-width: clamp(14px, 3.2vw, 28px);
      display: grid;
      grid-template-columns: 40px repeat(var(--cells), minmax(var(--cell-width), 1fr));
      gap: 1px;
      min-width: max-content;
      user-select: none;
      touch-action: none;
    }
    .hour {
      color: var(--text-muted);
      font-size: var(--font-body);
      text-align: center;
      grid-row: 1;
    }
    .day {
      align-self: center;
      color: var(--text-secondary);
      font-size: var(--font-body);
    }
    .cell {
      height: 26px;
      min-width: 0;
      padding: 0;
      border: 0;
      border-radius: 2px;
      background: var(--surface-muted);
      cursor: pointer;
    }
    .cell_on {
      background: var(--accent);
    }
    .cell_partial {
      background: linear-gradient(135deg, var(--accent) 50%, var(--surface-muted) 50%);
    }
    .cell:focus-visible {
      outline: 2px solid var(--accent-strong);
      outline-offset: 1px;
    }
    .cell:disabled {
      cursor: not-allowed;
      opacity: 0.5;
    }
  `,
})
export class WeeklyScheduleGridComponent {
  readonly slots = input.required<string>();
  readonly interval = input<ScheduleInterval>(60);
  readonly disabled = input(false);
  readonly label = input('Calendrier hebdomadaire');
  readonly slotsChange = output<string>();

  protected readonly days = SCHEDULE_DAYS;
  protected readonly cells = computed(() =>
    Array.from({ length: cellsPerDay(this.interval()) }, (_, index) => index),
  );
  protected readonly hours = computed(() => {
    const perHour = 60 / this.interval();
    return Array.from({ length: 24 }, (_, index) => ({
      index,
      label: String(index),
      column: `${index * perHour + 2} / span ${perHour}`,
    }));
  });

  private painting: { day: number; last: number; open: boolean } | null = null;

  protected state(day: number, cell: number): CellState {
    return cellState(this.slots(), day, cell, this.interval());
  }

  protected cellLabel(day: number, cell: number): string {
    const minutes = cell * this.interval();
    const time = `${String(Math.floor(minutes / 60)).padStart(2, '0')}:${String(minutes % 60).padStart(2, '0')}`;
    return `${SCHEDULE_DAYS[day]} ${time}, ${this.state(day, cell) === 'on' ? 'autorisé' : 'bloqué'}`;
  }

  protected startPaint(event: PointerEvent, day: number, cell: number): void {
    if (this.disabled() || event.button > 0) return;
    event.preventDefault();
    const open = this.state(day, cell) !== 'on';
    this.painting = { day, last: cell, open };
    this.paint(day, cell, cell, open);
  }

  protected onPointerMove(event: PointerEvent): void {
    const painting = this.painting;
    if (!painting) return;
    const target = document.elementFromPoint(event.clientX, event.clientY) as HTMLElement | null;
    if (target?.dataset['day'] !== String(painting.day) || target.dataset['cell'] === undefined)
      return;
    const cell = Number(target.dataset['cell']);
    if (cell === painting.last) return;
    this.paint(painting.day, painting.last, cell, painting.open);
    painting.last = cell;
  }

  protected endPaint(): void {
    this.painting = null;
  }

  protected toggleCell(day: number, cell: number): void {
    if (!this.disabled()) this.paint(day, cell, cell, this.state(day, cell) !== 'on');
  }

  private paint(day: number, from: number, to: number, open: boolean): void {
    this.slotsChange.emit(paintCells(this.slots(), day, from, to, this.interval(), open));
  }
}
