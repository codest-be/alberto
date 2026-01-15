import { Component, inject, OnInit, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AdminApiService } from '../../core/services/admin-api.service';
import { SystemInfo, ProcessorStatus } from '../../core/models/admin.models';

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, DecimalPipe],
  template: `
    <div class="dashboard">
      <header class="page-header">
        <h1>Dashboard</h1>
        <p class="subtitle">Event sourcing system overview</p>
      </header>

      @if (loading()) {
        <div class="loading">Loading...</div>
      } @else if (error()) {
        <div class="error">
          <p>Failed to load system info</p>
          <p class="error-detail">{{ error() }}</p>
          <button class="btn" (click)="loadData()">Retry</button>
        </div>
      } @else {
        <div class="stats-grid">
          <div class="stat-card">
            <span class="stat-label">Global Position</span>
            <span class="stat-value">{{ systemInfo()?.globalPosition | number }}</span>
          </div>
          <div class="stat-card">
            <span class="stat-label">Processors</span>
            <span class="stat-value">{{ systemInfo()?.processorCount }}</span>
          </div>
          <div class="stat-card" [class.warning]="(systemInfo()?.deadLetterCount ?? 0) > 0">
            <span class="stat-label">Dead Letters</span>
            <span class="stat-value">{{ systemInfo()?.deadLetterCount }}</span>
          </div>
          <div class="stat-card">
            <span class="stat-label">Mode</span>
            <span class="stat-value mode">{{
              systemInfo()?.readOnlyMode ? 'Read-Only' : 'Active'
            }}</span>
          </div>
        </div>

        <section class="section">
          <div class="section-header">
            <h2>Processors</h2>
            <a routerLink="/processors" class="link">View all</a>
          </div>
          @if (processors().length === 0) {
            <p class="empty">No processors configured</p>
          } @else {
            <div class="processors-grid">
              @for (processor of processors(); track processor.processorId) {
                <div class="processor-card" [class.inactive]="!processor.isActive">
                  <div class="processor-header">
                    <span class="processor-name">{{ processor.processorId }}</span>
                    <span class="status-badge" [class.active]="processor.isActive">
                      {{ processor.isActive ? 'Active' : 'Inactive' }}
                    </span>
                  </div>
                  <div class="processor-stats">
                    <div class="processor-stat">
                      <span class="label">Position</span>
                      <span class="value">{{ processor.lastPosition ?? '-' }}</span>
                    </div>
                    <div class="processor-stat">
                      <span class="label">Lag</span>
                      <span class="value" [class.warning]="processor.lag > 100">{{
                        processor.lag
                      }}</span>
                    </div>
                    <div class="processor-stat">
                      <span class="label">Dead Letters</span>
                      <span class="value" [class.error]="processor.deadLetterCount > 0">{{
                        processor.deadLetterCount
                      }}</span>
                    </div>
                  </div>
                </div>
              }
            </div>
          }
        </section>
      }
    </div>
  `,
  styles: `
    .dashboard {
      max-width: 1200px;
    }

    .page-header {
      margin-bottom: 2rem;
    }

    h1 {
      font-size: 1.75rem;
      font-weight: 600;
      color: #fff;
      margin-bottom: 0.25rem;
    }

    .subtitle {
      color: #666;
    }

    .loading,
    .error {
      padding: 2rem;
      text-align: center;
      background: #1a1a1a;
      border-radius: 8px;
    }

    .error {
      color: #ef4444;
    }

    .error-detail {
      color: #666;
      font-size: 0.875rem;
      margin: 0.5rem 0 1rem;
    }

    .btn {
      padding: 0.5rem 1rem;
      background: #252525;
      border: 1px solid #333;
      border-radius: 6px;
      color: #e0e0e0;
      cursor: pointer;
      font-size: 0.875rem;

      &:hover {
        background: #333;
      }
    }

    .stats-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 1rem;
      margin-bottom: 2rem;
    }

    .stat-card {
      background: #1a1a1a;
      border: 1px solid #2a2a2a;
      border-radius: 8px;
      padding: 1.25rem;
      display: flex;
      flex-direction: column;
      gap: 0.5rem;

      &.warning {
        border-color: #f59e0b;

        .stat-value {
          color: #f59e0b;
        }
      }
    }

    .stat-label {
      font-size: 0.75rem;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: #666;
    }

    .stat-value {
      font-size: 1.5rem;
      font-weight: 600;
      color: #fff;

      &.mode {
        font-size: 1rem;
      }
    }

    .section {
      margin-top: 2rem;
    }

    .section-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 1rem;
    }

    h2 {
      font-size: 1.125rem;
      font-weight: 600;
      color: #fff;
    }

    .link {
      color: #8b5cf6;
      text-decoration: none;
      font-size: 0.875rem;

      &:hover {
        text-decoration: underline;
      }
    }

    .empty {
      color: #666;
      text-align: center;
      padding: 2rem;
      background: #1a1a1a;
      border-radius: 8px;
    }

    .processors-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
      gap: 1rem;
    }

    .processor-card {
      background: #1a1a1a;
      border: 1px solid #2a2a2a;
      border-radius: 8px;
      padding: 1rem;

      &.inactive {
        opacity: 0.6;
      }
    }

    .processor-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 1rem;
    }

    .processor-name {
      font-weight: 500;
      color: #fff;
    }

    .status-badge {
      font-size: 0.75rem;
      padding: 0.25rem 0.5rem;
      border-radius: 4px;
      background: #333;
      color: #888;

      &.active {
        background: rgba(34, 197, 94, 0.15);
        color: #22c55e;
      }
    }

    .processor-stats {
      display: flex;
      gap: 1.5rem;
    }

    .processor-stat {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;

      .label {
        font-size: 0.75rem;
        color: #666;
      }

      .value {
        font-weight: 500;
        color: #e0e0e0;

        &.warning {
          color: #f59e0b;
        }

        &.error {
          color: #ef4444;
        }
      }
    }
  `,
})
export class DashboardComponent implements OnInit {
  private readonly api = inject(AdminApiService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly systemInfo = signal<SystemInfo | null>(null);
  readonly processors = signal<ProcessorStatus[]>([]);

  ngOnInit(): void {
    this.loadData();
  }

  loadData(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.getSystemInfo().subscribe({
      next: (info) => {
        this.systemInfo.set(info);
        this.loadProcessors();
      },
      error: (err) => {
        this.error.set(err.message || 'Unknown error');
        this.loading.set(false);
      },
    });
  }

  private loadProcessors(): void {
    this.api.getProcessors().subscribe({
      next: (processors) => {
        this.processors.set(processors);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err.message || 'Failed to load processors');
        this.loading.set(false);
      },
    });
  }
}
