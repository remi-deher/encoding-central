/**
 * Utilitaires de formatage de date et heure pour alléger l'affichage des tableaux et listes.
 */

export type DateFormat = 'short' | 'relative' | 'date';

function parseDate(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

function formatParts(value: string | number | Date | null | undefined, withTime: boolean): string {
  const date = parseDate(value);
  if (!date) return '—';
  const parts = new Intl.DateTimeFormat('fr-FR', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    ...(withTime ? { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' as const } : {}),
  }).formatToParts(date);
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((item) => item.type === type)?.value;
  const formattedDate = `${part('day')}/${part('month')}/${part('year')}`;
  return withTime ? `${formattedDate} ${part('hour')}:${part('minute')}` : formattedDate;
}

/** Format court partagé : dd/MM/yyyy HH:mm en locale française. */
export function formatShortDateTime(value: string | number | Date | null | undefined): string {
  return formatParts(value, true);
}

/** Date seule partagée : dd/MM/yyyy en locale française. */
export function formatDateOnly(value: string | number | Date | null | undefined): string {
  return formatParts(value, false);
}

/** Alias historique conservé pour les composants existants. */
export function formatFullDateTime(value: string | number | Date | null | undefined): string {
  return formatShortDateTime(value);
}

export function formatRelativeTime(
  value: string | number | Date | null | undefined,
  now: number = Date.now(),
): string {
  const date = parseDate(value);
  if (!date) return '—';
  const time = date.getTime();

  const diffMs = now - time;
  // If slightly in the future (within 30s) or very recent:
  if (Math.abs(diffMs) < 45_000) {
    return 'À l’instant';
  }

  const future = diffMs < 0;
  const elapsedSeconds = Math.abs(Math.floor(diffMs / 1000));
  const relative = (amount: number, unit: string) =>
    future ? `Dans ${amount} ${unit}` : `Il y a ${amount} ${unit}`;
  if (elapsedSeconds < 3600) {
    const mins = Math.max(1, Math.floor(elapsedSeconds / 60));
    return relative(mins, 'min');
  }

  if (elapsedSeconds < 86_400) {
    const hours = Math.floor(elapsedSeconds / 3600);
    return relative(hours, 'h');
  }

  const hoursStr = String(date.getHours()).padStart(2, '0');
  const minutesStr = String(date.getMinutes()).padStart(2, '0');
  const timeStr = `${hoursStr}:${minutesStr}`;

  if (elapsedSeconds < 172_800) {
    return future ? `Demain à ${timeStr}` : `Hier à ${timeStr}`;
  }

  const day = date.getDate();
  const monthNames = [
    'janv.',
    'févr.',
    'mars',
    'avr.',
    'mai',
    'juin',
    'juil.',
    'août',
    'sept.',
    'oct.',
    'nov.',
    'déc.',
  ];
  const month = monthNames[date.getMonth()];
  return `${day} ${month} à ${timeStr}`;
}
