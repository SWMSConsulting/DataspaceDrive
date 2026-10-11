import { Component } from '@angular/core';

import { PlannedFeature } from '../../shared/planned-feature/planned-feature';

@Component({
  selector: 'app-inbox',
  imports: [PlannedFeature],
  templateUrl: './inbox.html',
  styleUrl: './inbox.scss',
})
export class Inbox {
  readonly items = [
    'Neue Angebote mit Absender, Nachricht, Dateien und Ablauf',
    'Annahme mit Zielordner, Ablehnung',
    'Fortschritt: Verhandlung, Download, Prüfsumme, Virenscan',
    'Ablage unter „Von Partnern/<Partner>“',
  ];
}
