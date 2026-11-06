import { Pipe, PipeTransform } from '@angular/core';
import {
  DateFormat,
  formatDateOnly,
  formatRelativeTime,
  formatShortDateTime,
} from '../models/date-format';

@Pipe({ name: 'appDate', standalone: true, pure: true })
export class AppDatePipe implements PipeTransform {
  transform(
    value: string | number | Date | null | undefined,
    format: DateFormat = 'short',
  ): string {
    switch (format) {
      case 'relative':
        return formatRelativeTime(value);
      case 'date':
        return formatDateOnly(value);
      case 'short':
        return formatShortDateTime(value);
    }
  }
}
