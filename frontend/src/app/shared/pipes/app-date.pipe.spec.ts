import { AppDatePipe } from './app-date.pipe';

describe('AppDatePipe', () => {
  it('formats short date and time in French', () => {
    expect(new AppDatePipe().transform('2026-10-18T12:34:00Z')).toMatch(
      /^\d{2}\/\d{2}\/2026 \d{2}:34$/,
    );
  });

  it('supports relative, date-only and empty values', () => {
    const pipe = new AppDatePipe();
    expect(pipe.transform('2026-10-18T12:34:00Z', 'date')).toMatch(/^\d{2}\/\d{2}\/2026$/);
    expect(pipe.transform(null, 'relative')).toBe('—');
  });
});
