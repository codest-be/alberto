import { Component, inject, OnInit, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, Router } from '@angular/router';
import { AdminApiService } from '../../../core/services/admin-api.service';
import { ModuleInfo } from '../../../core/models/admin.models';

@Component({
  selector: 'app-shell',
  imports: [RouterLink, RouterLinkActive],
  template: `
    <div class="shell">
      <aside class="sidebar">
        <div class="logo">
          <span class="logo-icon">A</span>
          <span class="logo-text">Alberto Admin</span>
        </div>
        <nav class="nav">
          @for (item of navItems; track item.path) {
            <a
              class="nav-item"
              [routerLink]="item.path"
              routerLinkActive="active"
              [routerLinkActiveOptions]="{ exact: item.path === '/dashboard' }"
            >
              <span class="nav-icon">{{ item.icon }}</span>
              <span class="nav-label">{{ item.label }}</span>
            </a>
          }
        </nav>
        <div class="sidebar-footer">
          @if (modules().length > 1) {
            <select
              class="module-select"
              [value]="api.moduleKey()"
              (change)="switchModule($event)"
            >
              @for (module of modules(); track module.moduleKey) {
                <option [value]="module.moduleKey">{{ module.title }}</option>
              }
            </select>
          } @else {
            <span class="module-badge">{{ api.moduleKey() }}</span>
          }
        </div>
      </aside>
      <main class="content">
        <ng-content />
      </main>
    </div>
  `,
  styles: `
    .shell {
      display: flex;
      height: 100vh;
      background: #0f0f0f;
      color: #e0e0e0;
    }

    .sidebar {
      width: 240px;
      background: #1a1a1a;
      border-right: 1px solid #2a2a2a;
      display: flex;
      flex-direction: column;
      flex-shrink: 0;
    }

    .logo {
      padding: 1.5rem;
      display: flex;
      align-items: center;
      gap: 0.75rem;
      border-bottom: 1px solid #2a2a2a;
    }

    .logo-icon {
      width: 32px;
      height: 32px;
      background: linear-gradient(135deg, #6366f1, #8b5cf6);
      border-radius: 8px;
      display: flex;
      align-items: center;
      justify-content: center;
      font-weight: 700;
      font-size: 1.125rem;
      color: white;
    }

    .logo-text {
      font-weight: 600;
      font-size: 1.125rem;
      color: #fff;
    }

    .nav {
      flex: 1;
      padding: 1rem 0.75rem;
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
    }

    .nav-item {
      display: flex;
      align-items: center;
      gap: 0.75rem;
      padding: 0.75rem 1rem;
      border-radius: 8px;
      text-decoration: none;
      color: #888;
      transition: all 0.15s ease;

      &:hover {
        background: #252525;
        color: #e0e0e0;
      }

      &.active {
        background: #252525;
        color: #fff;

        .nav-icon {
          color: #8b5cf6;
        }
      }
    }

    .nav-icon {
      font-size: 1.125rem;
      width: 24px;
      text-align: center;
    }

    .nav-label {
      font-size: 0.875rem;
      font-weight: 500;
    }

    .sidebar-footer {
      padding: 1rem 1.5rem;
      border-top: 1px solid #2a2a2a;
    }

    .module-badge {
      display: inline-block;
      padding: 0.25rem 0.75rem;
      background: #252525;
      border-radius: 4px;
      font-size: 0.75rem;
      font-weight: 500;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: #888;
    }

    .module-select {
      width: 100%;
      padding: 0.5rem 0.75rem;
      background: #252525;
      border: 1px solid #3a3a3a;
      border-radius: 6px;
      font-size: 0.875rem;
      font-weight: 500;
      color: #e0e0e0;
      cursor: pointer;
      transition: all 0.15s ease;

      &:hover {
        border-color: #4a4a4a;
      }

      &:focus {
        outline: none;
        border-color: #6366f1;
      }

      option {
        background: #1a1a1a;
        color: #e0e0e0;
      }
    }

    .content {
      flex: 1;
      overflow-y: auto;
      padding: 2rem;
    }
  `,
})
export class ShellComponent implements OnInit {
  readonly api = inject(AdminApiService);
  private readonly router = inject(Router);

  readonly modules = signal<ModuleInfo[]>([]);

  navItems = [
    { path: '/dashboard', label: 'Dashboard', icon: '~' },
    { path: '/processors', label: 'Processors', icon: '>' },
    { path: '/checkpoints', label: 'Checkpoints', icon: '#' },
    { path: '/dead-letters', label: 'Dead Letters', icon: '!' },
    { path: '/projections', label: 'Projections', icon: '@' },
  ];

  ngOnInit(): void {
    this.loadModules();
    this.restoreSelectedModule();
  }

  private loadModules(): void {
    this.api.getModules().subscribe({
      next: (modules) => {
        this.modules.set(modules);
      },
      error: (err) => {
        console.error('Failed to load modules:', err);
      },
    });
  }

  private restoreSelectedModule(): void {
    const savedModule = localStorage.getItem('selectedModule');
    if (savedModule) {
      this.api.moduleKey.set(savedModule);
    }
  }

  switchModule(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const moduleKey = select.value;

    this.api.moduleKey.set(moduleKey);
    localStorage.setItem('selectedModule', moduleKey);

    // Navigate to dashboard to refresh data for new module
    this.router.navigate(['/dashboard']);
  }
}
