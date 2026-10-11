import { Component, inject } from '@angular/core';

import { ConfigService } from '../../core/config.service';

@Component({
  selector: 'app-settings',
  templateUrl: './settings.html',
  styleUrl: './settings.scss',
})
export class Settings {
  private readonly configService = inject(ConfigService);

  readonly config = this.configService.config;
  readonly unavailable = this.configService.unavailable;
}
