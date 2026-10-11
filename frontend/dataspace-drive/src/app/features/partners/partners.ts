import { Component } from '@angular/core';

import { PlannedFeature } from '../../shared/planned-feature/planned-feature';

@Component({
  selector: 'app-partners',
  imports: [PlannedFeature],
  templateUrl: './partners.html',
  styleUrl: './partners.scss',
})
export class Partners {
  readonly items = [
    'Verzeichnis aus dem BDRS des Dataspace',
    'Freischaltung für Freigabe und Empfang',
    'Sperre mit sofortiger Wirkung',
  ];
}
