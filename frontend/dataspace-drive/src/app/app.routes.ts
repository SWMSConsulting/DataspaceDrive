import { Routes } from '@angular/router';

import { Shell } from './shell/shell';

export const routes: Routes = [
  {
    path: '',
    component: Shell,
    children: [
      { path: '', redirectTo: 'files', pathMatch: 'full' },
      { path: 'files', loadComponent: () => import('./features/files/files').then((m) => m.Files) },
      { path: 'shares', loadComponent: () => import('./features/shares/shares').then((m) => m.Shares) },
      { path: 'inbox', loadComponent: () => import('./features/inbox/inbox').then((m) => m.Inbox) },
      { path: 'partners', loadComponent: () => import('./features/partners/partners').then((m) => m.Partners) },
      { path: 'activity', loadComponent: () => import('./features/activity/activity').then((m) => m.Activity) },
      { path: 'admin', loadComponent: () => import('./features/admin/admin').then((m) => m.Admin) },
      { path: 'settings', loadComponent: () => import('./features/settings/settings').then((m) => m.Settings) },
    ],
  },
  { path: '**', redirectTo: '' },
];
