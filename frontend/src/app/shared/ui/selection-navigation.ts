/** Index de navigation partagé par les filtres et les onglets (activation automatique). */
export function selectionNavigationIndex(key: string, index: number, count: number): number {
  if (index < 0 || count === 0) return -1;
  switch (key) {
    case 'ArrowRight':
      return (index + 1) % count;
    case 'ArrowLeft':
      return (index - 1 + count) % count;
    case 'Home':
      return 0;
    case 'End':
      return count - 1;
    default:
      return -1;
  }
}
