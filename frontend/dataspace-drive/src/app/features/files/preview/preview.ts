import { Component, computed, inject, input, output } from '@angular/core';
import { DomSanitizer } from '@angular/platform-browser';
import { DxPopupModule } from 'devextreme-angular/ui/popup';

import { downloadFile } from '../../../shared/format';

export interface PreviewFile {
  id: string;
  name: string;
  contentType: string | null;
}

const images = ['image/png', 'image/jpeg', 'image/gif', 'image/webp'];

/** Gleiche Typen wie im Backend (FilePreview): Rastergrafiken, PDF, Klartext. */
export function previewable(contentType: string | null): boolean {
  const type = (contentType ?? '').split(';')[0].trim().toLowerCase();
  return images.includes(type) || type === 'application/pdf' || type === 'text/plain' || type === 'text/csv';
}

/** Vorschau einer Datei im Fenster; Bilder direkt, PDF und Text im eingebetteten Rahmen. */
@Component({
  selector: 'app-preview',
  imports: [DxPopupModule],
  templateUrl: './preview.html',
  styleUrl: './preview.scss',
})
export class Preview {
  private readonly sanitizer = inject(DomSanitizer);

  readonly file = input<PreviewFile | null>(null);
  readonly closed = output<void>();

  readonly url = computed(() => {
    const file = this.file();
    return file ? `/api/files/${file.id}/preview` : '';
  });
  readonly isImage = computed(() => images.includes((this.file()?.contentType ?? '').split(';')[0].trim().toLowerCase()));
  // Eigene, relative Adresse: als Ressource für den eingebetteten Rahmen freigeben.
  readonly frameUrl = computed(() => this.sanitizer.bypassSecurityTrustResourceUrl(this.url()));

  readonly downloadButtonOptions = {
    text: 'Herunterladen',
    icon: 'download',
    stylingMode: 'outlined',
    onClick: () => {
      const file = this.file();
      if (file) {
        downloadFile(`/api/files/${file.id}/content`);
      }
    },
  };
}
