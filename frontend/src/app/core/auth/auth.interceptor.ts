import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject, Injector } from '@angular/core';
import { catchError, retry, throwError, timer } from 'rxjs';
import { isAuthEndpoint, isStateChangingMethod } from '../../shared/models/auth';
import { NotificationService } from '../../shared/services/notification.service';
import { AuthService } from './auth.service';

/** En-tête exigé par le serveur pour toute modification authentifiée par cookie (protection inter-sites). */
export const CSRF_HEADER = 'X-Encoding-Csrf';

function extractProblemTitle(error: HttpErrorResponse): string | null {
  const payload = error.error;
  if (typeof payload === 'object' && payload !== null && 'title' in payload) {
    const title = (payload as { title?: unknown }).title;
    if (typeof title === 'string' && title.trim().length > 0) return title;
  }
  return null;
}

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.url.startsWith('/api/')) return next(request);
  const injector = inject(Injector);
  const outgoing = isStateChangingMethod(request.method)
    ? request.clone({ setHeaders: { [CSRF_HEADER]: '1' } })
    : request;
  return next(outgoing).pipe(
    retry({
      count: 1,
      delay: (error) => {
        if (
          error instanceof HttpErrorResponse &&
          error.status === 503 &&
          (error.error?.code === 'sqlite_write_unavailable' ||
            (typeof error.error?.detail === 'string' && error.error.detail.includes('court délai')))
        ) {
          const retryAfter = error.headers?.get('Retry-After');
          const delayMs = retryAfter ? Math.max(1, parseInt(retryAfter, 10)) * 1000 : 1000;
          return timer(delayMs);
        }
        return throwError(() => error);
      },
    }),
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && !isAuthEndpoint(request.url)) {
        if (error.status === 401) injector.get(AuthService).sessionExpired();
        else if (error.status === 403)
          injector.get(NotificationService).show('Cette action est réservée aux administrateurs.');
        else if (error.status === 429)
          injector
            .get(NotificationService)
            .show(
              extractProblemTitle(error) ??
                'Trop de requêtes. Veuillez réessayer dans quelques instants.',
            );
      }
      return throwError(() => error);
    }),
  );
};
