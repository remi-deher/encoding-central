const UNITS = ['Ko', 'Mo', 'Go', 'To'] as const;

/** Format a byte count with French, human-readable binary units. */
export function formatFileSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '—';
  if (bytes < 1024) {
    const count = Math.trunc(bytes);
    return `${count.toLocaleString('fr-FR')} ${count === 1 ? 'octet' : 'octets'}`;
  }

  let value = bytes;
  let unitIndex = -1;
  do {
    value /= 1024;
    unitIndex++;
  } while (value >= 1024 && unitIndex < UNITS.length - 1);

  return `${new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 1 }).format(value)} ${UNITS[unitIndex]}`;
}
