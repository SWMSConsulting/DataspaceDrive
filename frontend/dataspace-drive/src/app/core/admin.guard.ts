import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from './auth.service';

/** Läuft unterhalb des authGuard; der Nutzer ist also schon geladen. */
export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.me()?.isAdmin ? true : inject(Router).createUrlTree(['/files']);
};
