import DataGrid from 'devextreme/ui/data_grid';
import Popup from 'devextreme/ui/popup';

/**
 * Einheitliches Erscheinungsbild der DevExtreme-Widgets: Tabellen ohne Gitter, nur mit Zeilentrennern; Dialoge erscheinen
 * weich. Die Seiten importieren diese Datei selbst, damit Tabelle und Dialog nicht im initialen Bundle landen.
 */
DataGrid.defaultOptions({
  options: { showBorders: false, showColumnLines: false, showRowLines: true, hoverStateEnabled: true },
});

Popup.defaultOptions({
  options: {
    shadingColor: 'rgba(0, 0, 0, 0.28)',
    animation: {
      show: { type: 'pop', duration: 260, from: { scale: 0.96, opacity: 0 }, to: { scale: 1, opacity: 1 } },
      hide: { type: 'fade', duration: 150, from: 1, to: 0 },
    },
  },
});
