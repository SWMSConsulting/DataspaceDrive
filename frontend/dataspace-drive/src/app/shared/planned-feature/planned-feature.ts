import { Component, input } from '@angular/core';

/** Platzhalter für einen Bereich, der in einer späteren Phase umgesetzt wird. */
@Component({
  selector: 'app-planned-feature',
  templateUrl: './planned-feature.html',
  styleUrl: './planned-feature.scss',
})
export class PlannedFeature {
  readonly heading = input.required<string>();
  readonly phase = input.required<string>();
  readonly description = input.required<string>();
  readonly items = input<string[]>([]);
}
