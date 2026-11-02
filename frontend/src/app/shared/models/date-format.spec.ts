import { formatFullDateTime, formatRelativeTime } from './date-format';

describe('date-format', () => {
  const baseTime = new Date('2026-10-18T12:00:00Z').getTime();

  describe('formatRelativeTime', () => {
    it('returns — for null or undefined', () => {
      expect(formatRelativeTime(null)).toBe('—');
      expect(formatRelativeTime(undefined)).toBe('—');
      expect(formatRelativeTime('')).toBe('—');
    });

    it('returns À l’instant for dates within 45s', () => {
      expect(formatRelativeTime(baseTime - 10_000, baseTime)).toBe('À l’instant');
      expect(formatRelativeTime(baseTime, baseTime)).toBe('À l’instant');
    });

    it('returns minutes for dates within 1 hour', () => {
      expect(formatRelativeTime(baseTime - 5 * 60_000, baseTime)).toBe('Il y a 5 min');
      expect(formatRelativeTime(baseTime - 59 * 60_000, baseTime)).toBe('Il y a 59 min');
    });

    it('returns hours for dates within 24 hours', () => {
      expect(formatRelativeTime(baseTime - 2 * 3600_000, baseTime)).toBe('Il y a 2 h');
      expect(formatRelativeTime(baseTime - 23 * 3600_000, baseTime)).toBe('Il y a 23 h');
    });

    it('returns Hier for dates between 24 and 48 hours', () => {
      const yesterday = new Date(baseTime - 26 * 3600_000);
      expect(formatRelativeTime(yesterday, baseTime)).toMatch(/^Hier à \d{2}:\d{2}$/);
    });

    it('returns short formatted date for older dates', () => {
      const older = new Date(baseTime - 5 * 86_400_000);
      expect(formatRelativeTime(older, baseTime)).toContain('oct.');
    });
  });

  describe('formatFullDateTime', () => {
    it('returns — for null or invalid dates', () => {
      expect(formatFullDateTime(null)).toBe('—');
      expect(formatFullDateTime('invalid-date')).toBe('—');
    });

    it('formats valid dates with localized representation', () => {
      const result = formatFullDateTime('2026-10-18T10:00:00Z');
      expect(result).not.toBe('—');
      expect(result).toContain('2026');
    });
  });
});
