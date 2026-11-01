import { HttpErrorResponse } from '@angular/common/http';
import { ApiErrorBody } from './models';

export function apiErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as ApiErrorBody | null;
    if (body && typeof body.error === 'string' && body.error.length > 0) return body.error;
  }
  return fallback;
}
