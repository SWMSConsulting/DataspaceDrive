import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import { AuthService } from './auth.service';

/** Abgelaufene Sitzung: zurück zur Anmeldung, danach landet der Nutzer wieder auf der aktuellen Seite. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  return next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && req.url.startsWith('/api/')) {
        auth.login();
      }
      return throwError(() => error);
    }),
  );
};
