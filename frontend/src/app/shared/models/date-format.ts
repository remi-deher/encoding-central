/**
 * Utilitaires de formatage de date et heure pour alléger l'affichage des tableaux et listes.
 */

export function formatFullDateTime(value: string | number | Date | null | undefined): string {
  if (!value) return '—';
  const date = value instanceof Date ? value : new Date(value);
  if (isNaN(date.getTime())) return '—';
  return date.toLocaleString('fr-FR');
}

export function formatRelativeTime(
  value: string | number | Date | null | undefined,
  now: number = Date.now(),
): string {
  if (!value) return '—';
  const date = value instanceof Date ? value : new Date(value);
  const time = date.getTime();
  if (isNaN(time)) return '—';

  const diffMs = now - time;
  // If slightly in the future (within 30s) or very recent:
  if (diffMs < 45_000 && diffMs >= -30_000) {
    return 'À l’instant';
  }

  const diffSeconds = Math.floor(diffMs / 1000);
  if (diffSeconds < 3600) {
    const mins = Math.max(1, Math.floor(diffSeconds / 60));
    return `Il y a ${mins} min`;
  }

  if (diffSeconds < 86_400) {
    const hours = Math.floor(diffSeconds / 3600);
    return `Il y a ${hours} h`;
  }

  const hoursStr = String(date.getHours()).padStart(2, '0');
  const minutesStr = String(date.getMinutes()).padStart(2, '0');
  const timeStr = `${hoursStr}:${minutesStr}`;

  if (diffSeconds < 172_800) {
    return `Hier à ${timeStr}`;
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
