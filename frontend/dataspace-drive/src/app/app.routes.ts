import { Routes } from '@angular/router';

import { adminGuard } from './core/admin.guard';
import { authGuard } from './core/auth.guard';
import { Shell } from './shell/shell';

export const routes: Routes = [
  { path: 'no-access', loadComponent: () => import('./no-access/no-access').then((m) => m.NoAccess) },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', redirectTo: 'files', pathMatch: 'full' },
      { path: 'files', loadComponent: () => import('./features/files/files').then((m) => m.Files) },
      { path: 'trash', loadComponent: () => import('./features/trash/trash').then((m) => m.Trash) },
      { path: 'shares', loadComponent: () => import('./features/shares/shares').then((m) => m.Shares) },
      { path: 'inbox', loadComponent: () => import('./features/inbox/inbox').then((m) => m.Inbox) },
      { path: 'partners', loadComponent: () => import('./features/partners/partners').then((m) => m.Partners) },
      { path: 'activity', loadComponent: () => import('./features/activity/activity').then((m) => m.Activity) },
      { path: 'admin', canActivate: [adminGuard], loadComponent: () => import('./features/admin/admin').then((m) => m.Admin) },
      { path: 'settings', loadComponent: () => import('./features/settings/settings').then((m) => m.Settings) },
    ],
  },
  { path: '**', redirectTo: '' },
];
