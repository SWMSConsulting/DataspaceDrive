import { Component } from '@angular/core';
import { DxFileManagerModule } from 'devextreme-angular/ui/file-manager';
import CustomFileSystemProvider from 'devextreme/file_management/custom_provider';

import { PlannedFeature } from '../../shared/planned-feature/planned-feature';

@Component({
  selector: 'app-files',
  imports: [DxFileManagerModule, PlannedFeature],
  templateUrl: './files.html',
  styleUrl: './files.scss',
})
export class Files {
  // Bis zur Ablage-API (Phase 1) liefert der Provider einen leeren Bereich. Später ersetzen
  // getItems/uploadFileChunk/downloadItems die Aufrufe gegen /api/folders, /api/uploads und /api/files.
  readonly fileProvider = new CustomFileSystemProvider({
    getItems: () => Promise.resolve([]),
  });

  readonly permissions = {
    create: false,
    copy: false,
    move: false,
    delete: false,
    rename: false,
    upload: false,
    download: false,
  };

  readonly items = [
    'Persönlicher Bereich und Team-Bereiche mit Ordnern',
    'Hochladen in Teilstücken, auch für mehrere Gigabyte',
    'Versionen, Papierkorb und Suche',
    'Virenscan und Prüfsumme (SHA-256) für jede Datei',
  ];
}
