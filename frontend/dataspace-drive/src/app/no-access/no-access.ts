import { Component, inject } from '@angular/core';
import { DxButtonModule } from 'devextreme-angular/ui/button';

import { AuthService } from '../core/auth.service';

/** Angemeldet, aber ohne Rolle drive-user: kein Zugriff auf die Ablage. */
@Component({
  selector: 'app-no-access',
  imports: [DxButtonModule],
  templateUrl: './no-access.html',
  styleUrl: './no-access.scss',
})
export class NoAccess {
  readonly auth = inject(AuthService);
}
