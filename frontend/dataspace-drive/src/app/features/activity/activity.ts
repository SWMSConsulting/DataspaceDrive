import { Component } from '@angular/core';

import { PlannedFeature } from '../../shared/planned-feature/planned-feature';

@Component({
  selector: 'app-activity',
  imports: [PlannedFeature],
  templateUrl: './activity.html',
  styleUrl: './activity.scss',
})
export class Activity {
  readonly items = [
    'Wer hat was mit welchem Unternehmen geteilt',
    'Abrufe durch Partner mit Zeitpunkt und Vertrag',
    'Export als CSV',
  ];
}
