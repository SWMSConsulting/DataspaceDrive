import { bootstrapApplication } from '@angular/platform-browser';
import config from 'devextreme/core/config';
import { loadMessages, locale } from 'devextreme/localization';
import deMessages from 'devextreme/localization/messages/de.json';

import { appConfig } from './app/app.config';
import { App } from './app/app';
import { DEVEXTREME_LICENSE_KEY } from './license';

if (DEVEXTREME_LICENSE_KEY) {
  config({ licenseKey: DEVEXTREME_LICENSE_KEY });
}

// Eingebaute DevExtreme-Texte (FileManager, Grids, Dialoge) auf Deutsch.
loadMessages(deMessages);
locale('de');

bootstrapApplication(App, appConfig).catch((err) => console.error(err));
