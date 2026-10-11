import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (auth.me()) {
    return true;
  }
  try {
    await auth.load();
    return true;
  } catch (error) {
    // 401 behandelt der Interceptor (Weiterleitung zur Anmeldung); 403 heißt: angemeldet, aber ohne Drive-Rolle.
    if (error instanceof HttpErrorResponse && error.status === 403) {
      return router.createUrlTree(['/no-access']);
    }
    return false;
  }
};
