# DataspaceDrive – Frontend

Angular 22 mit DevExtreme 26.1. Überblick, Architektur und Deployment stehen im [Haupt-README](../../README.md).

```bash
npm ci
npm start                                  # Entwicklungsserver, /api und /healthz gehen an das Backend (Port 5280)
npx ng build --configuration production    # Build nach dist/dataspace-drive/browser
```

Konventionen:
- Jede Komponente hat getrennte `.ts`-, `.html`- und `.scss`-Dateien.
- UI-Texte sind deutsch; die eingebauten DevExtreme-Texte werden in `src/main.ts` auf Deutsch gestellt.
- Gemeinsame Seitenbausteine (`.page-header`, `.hint`, `.phase-badge`) liegen in `src/styles.scss`.
- `src/license.ts` bleibt im Repo leer. Der Docker-Build schreibt den DevExtreme-Schlüssel aus dem Build-Argument `DEVEXTREME_LICENSE` hinein.
