import { Component } from '@angular/core';

import { PlannedFeature } from '../../shared/planned-feature/planned-feature';

@Component({
  selector: 'app-admin',
  imports: [PlannedFeature],
  templateUrl: './admin.html',
  styleUrl: './admin.scss',
})
export class Admin {
  readonly items = [
    'Partner und Policy-Profile',
    'EDC-Status, Abgleich (Reconciliation) und ausstehende Operationen',
    'Quarantäne für verdächtige Dateien',
    'Nutzer und Rollen aus dem Identity Provider',
  ];
}
