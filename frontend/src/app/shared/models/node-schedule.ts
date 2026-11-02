/** Un caractère par quart d’heure, de dimanche 00:00 à samedi 23:45, comme le contrat serveur. */
export const SLOTS_PER_HOUR = 4;
export const SLOTS_PER_DAY = 24 * SLOTS_PER_HOUR;
export const SLOT_COUNT = 7 * SLOTS_PER_DAY;

export interface NodeSchedule {
  readonly enabled: boolean;
  readonly timeZone: string;
  readonly slots: string;
}

export type ScheduleInterval = 15 | 30 | 60;

export const SCHEDULE_DAYS = ['Dim', 'Lun', 'Mar', 'Mer', 'Jeu', 'Ven', 'Sam'] as const;

export function allSlots(open: boolean): string {
  return (open ? '1' : '0').repeat(SLOT_COUNT);
}

export function isValidSlots(slots: string): boolean {
  return slots.length === SLOT_COUNT && /^[01]+$/.test(slots);
}

export type CellState = 'on' | 'off' | 'partial';

function cellRange(day: number, cell: number, interval: ScheduleInterval): [number, number] {
  const width = interval / 15;
  const start = day * SLOTS_PER_DAY + cell * width;
  return [start, start + width];
}

/** Une cellule regroupe interval/15 créneaux ; elle est partielle si ceux-ci diffèrent. */
export function cellState(
  slots: string,
  day: number,
  cell: number,
  interval: ScheduleInterval,
): CellState {
  const [start, end] = cellRange(day, cell, interval);
  const covered = slots.slice(start, end);
  if (!covered.includes('0')) return 'on';
  return covered.includes('1') ? 'partial' : 'off';
}

/** Applique l’état aux cellules d’un même jour comprises entre deux indices inclus. */
export function paintCells(
  slots: string,
  day: number,
  fromCell: number,
  toCell: number,
  interval: ScheduleInterval,
  open: boolean,
): string {
  const first = Math.min(fromCell, toCell);
  const last = Math.max(fromCell, toCell);
  const [start] = cellRange(day, Math.max(first, 0), interval);
  const [, end] = cellRange(day, Math.min(last, cellsPerDay(interval) - 1), interval);
  return (
    slots.slice(0, start) + (open ? '1' : '0').repeat(Math.max(end - start, 0)) + slots.slice(end)
  );
}

export function paintDay(slots: string, day: number, open: boolean): string {
  return paintCells(slots, day, 0, SLOTS_PER_DAY - 1, 15, open);
}

export function cellsPerDay(interval: ScheduleInterval): number {
  return (24 * 60) / interval;
}

export function isOpenAt(schedule: NodeSchedule, day: number, minuteOfDay: number): boolean {
  return (
    !schedule.enabled || schedule.slots[day * SLOTS_PER_DAY + Math.floor(minuteOfDay / 15)] === '1'
  );
}
