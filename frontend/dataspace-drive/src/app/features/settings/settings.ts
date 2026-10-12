import { HttpClient } from '@angular/common/http';
import { Component, inject } from '@angular/core';
import { DxSwitchModule } from 'devextreme-angular/ui/switch';
import notify from 'devextreme/ui/notify';
import { firstValueFrom } from 'rxjs';

import { AuthService } from '../../core/auth.service';
import { ConfigService } from '../../core/config.service';
import { errorText } from '../../core/drive-api.service';

@Component({
  selector: 'app-settings',
  imports: [DxSwitchModule],
  templateUrl: './settings.html',
  styleUrl: './settings.scss',
})
export class Settings {
  private readonly configService = inject(ConfigService);

  readonly config = this.configService.config;
  readonly unavailable = this.configService.unavailable;
  readonly auth = inject(AuthService);
  private readonly http = inject(HttpClient);

  async setEmailNotifications(enabled: boolean): Promise<void> {
    const me = this.auth.me();
    if (!me || me.emailNotifications === enabled) {
      return;
    }
    try {
      await firstValueFrom(this.http.put('/api/me/preferences', { emailNotifications: enabled }));
      this.auth.me.set({ ...me, emailNotifications: enabled });
      notify(enabled ? 'E-Mail-Benachrichtigungen eingeschaltet.' : 'E-Mail-Benachrichtigungen ausgeschaltet.', 'success', 2500);
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }
}
