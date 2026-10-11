import { Component } from '@angular/core';

import { PlannedFeature } from '../../shared/planned-feature/planned-feature';

@Component({
  selector: 'app-shares',
  imports: [PlannedFeature],
  templateUrl: './shares.html',
  styleUrl: './shares.scss',
})
export class Shares {
  readonly items = [
    'Freigabe-Assistent: Dateien, Partner, Nachricht, Ablaufdatum',
    'Status je Empfänger: bereitgestellt, abgerufen, widerrufen, abgelaufen',
    'Abrufprotokoll mit BPN und Vertrags-ID',
    'Widerruf für alle oder einzelne Empfänger',
  ];
}
