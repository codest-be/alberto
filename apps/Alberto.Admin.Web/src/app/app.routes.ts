import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    redirectTo: 'dashboard',
    pathMatch: 'full',
  },
  {
    path: 'dashboard',
    loadComponent: () =>
      import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
  },
  {
    path: 'processors',
    loadComponent: () =>
      import('./features/processors/processors.component').then((m) => m.ProcessorsComponent),
  },
  {
    path: 'checkpoints',
    loadComponent: () =>
      import('./features/checkpoints/checkpoints.component').then((m) => m.CheckpointsComponent),
  },
  {
    path: 'dead-letters',
    loadComponent: () =>
      import('./features/dead-letters/dead-letters.component').then((m) => m.DeadLettersComponent),
  },
  {
    path: 'projections',
    loadComponent: () =>
      import('./features/projections/projections.component').then((m) => m.ProjectionsComponent),
  },
];
