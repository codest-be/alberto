import { Component, inject, OnInit, signal, DestroyRef, effect } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription } from 'rxjs';
import { AdminApiService } from '../../core/services/admin-api.service';
import { ProcessorSubscriptionService } from '../../core/graphql/processor-subscription.service';
import { ProcessorStatus } from '../../core/models/admin.models';

@Component({
  selector: 'app-processors',
  imports: [DatePipe],
  template: `
    <div class="processors">
      <header class="page-header">
        <div>
          <h1>Processors</h1>
          <p class="subtitle">Manage event processors and their state</p>
        </div>
        <div class="header-actions">
          <span class="live-indicator" [class.connected]="subscriptionActive()" [class.polling]="usingPolling()">
            <span class="live-dot"></span>
            @if (subscriptionActive()) {
              {{ usingPolling() ? 'Polling' : 'Live' }}
            } @else {
              Connecting...
            }
          </span>
          <button class="btn btn-secondary" (click)="loadData()">Refresh</button>
        </div>
      </header>

      @if (loading()) {
        <div class="loading">Loading processors...</div>
      } @else if (error()) {
        <div class="error">
          <p>{{ error() }}</p>
          <button class="btn" (click)="loadData()">Retry</button>
        </div>
      } @else if (processors().length === 0) {
        <div class="empty">No processors configured</div>
      } @else {
        <div class="table-container">
          <table class="table">
            <thead>
              <tr>
                <th>Processor ID</th>
                <th>Status</th>
                <th>Position</th>
                <th>Global</th>
                <th>Lag</th>
                <th>Dead Letters</th>
                <th>Last Updated</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              @for (processor of processors(); track processor.processorId) {
                <tr
                  [class.inactive]="!processor.isActive"
                  [class.updated]="recentlyUpdated().has(processor.processorId)"
                >
                  <td class="processor-id">
                    <span>{{ processor.processorId }}</span>
                    <span class="event-types">
                      {{ processor.handledEventTypes.length }} event type(s)
                    </span>
                  </td>
                  <td>
                    <span class="status-badge" [class.active]="processor.isActive">
                      {{ processor.isActive ? 'Active' : 'Inactive' }}
                    </span>
                  </td>
                  <td class="mono">{{ processor.lastPosition ?? '-' }}</td>
                  <td class="mono">{{ processor.globalPosition }}</td>
                  <td>
                    <span class="lag" [class.warning]="processor.lag > 100">
                      {{ processor.lag }}
                    </span>
                  </td>
                  <td>
                    <span [class.error]="processor.deadLetterCount > 0">
                      {{ processor.deadLetterCount }}
                    </span>
                  </td>
                  <td class="muted">
                    {{ processor.lastUpdated | date: 'short' }}
                  </td>
                  <td>
                    @if (processor.isActive) {
                      <button
                        class="btn btn-sm"
                        (click)="deactivate(processor.processorId)"
                        [disabled]="actionInProgress()"
                      >
                        Deactivate
                      </button>
                    } @else {
                      <button
                        class="btn btn-sm btn-primary"
                        (click)="activate(processor.processorId)"
                        [disabled]="actionInProgress()"
                      >
                        Activate
                      </button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        @if (selectedProcessor()) {
          <div class="detail-panel">
            <h3>{{ selectedProcessor()?.processorId }}</h3>
            <div class="event-types-list">
              <h4>Handled Event Types</h4>
              <ul>
                @for (type of selectedProcessor()?.handledEventTypes; track type) {
                  <li>{{ type }}</li>
                }
              </ul>
            </div>
          </div>
        }
      }
    </div>
  `,
  styles: `
    .processors {
      width: 100%;
    }

    .page-header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      margin-bottom: 2rem;
    }

    .header-actions {
      display: flex;
      align-items: center;
      gap: 1rem;
    }

    .live-indicator {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      font-size: 0.8125rem;
      color: #666;
      padding: 0.375rem 0.75rem;
      background: #1a1a1a;
      border-radius: 6px;

      &.connected {
        color: #22c55e;
      }

      &.polling {
        color: #f59e0b;
      }
    }

    .live-dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: #666;

      .connected & {
        background: #22c55e;
        animation: pulse 2s infinite;
      }

      .polling & {
        background: #f59e0b;
        animation: pulse 2s infinite;
      }
    }

    @keyframes pulse {
      0%,
      100% {
        opacity: 1;
      }
      50% {
        opacity: 0.5;
      }
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
    .error,
    .empty {
      padding: 2rem;
      text-align: center;
      background: #1a1a1a;
      border-radius: 8px;
    }

    .error {
      color: #ef4444;
    }

    .btn {
      padding: 0.5rem 1rem;
      background: #252525;
      border: 1px solid #333;
      border-radius: 6px;
      color: #e0e0e0;
      cursor: pointer;
      font-size: 0.875rem;
      transition: background 0.15s;

      &:hover:not(:disabled) {
        background: #333;
      }

      &:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }

      &.btn-sm {
        padding: 0.375rem 0.75rem;
        font-size: 0.8125rem;
      }

      &.btn-primary {
        background: #6366f1;
        border-color: #6366f1;
        color: #fff;

        &:hover:not(:disabled) {
          background: #5558e3;
        }
      }

      &.btn-secondary {
        background: transparent;
      }
    }

    .table-container {
      background: #1a1a1a;
      border: 1px solid #2a2a2a;
      border-radius: 8px;
      overflow: hidden;
    }

    .table {
      width: 100%;
      border-collapse: collapse;

      th,
      td {
        padding: 0.875rem 1rem;
        text-align: left;
        border-bottom: 1px solid #2a2a2a;
      }

      th {
        font-weight: 500;
        font-size: 0.75rem;
        text-transform: uppercase;
        letter-spacing: 0.05em;
        color: #666;
        background: #151515;
      }

      tr:last-child td {
        border-bottom: none;
      }

      tr.inactive {
        opacity: 0.6;
      }

      tr.updated {
        animation: highlight 1s ease-out;
      }
    }

    @keyframes highlight {
      0% {
        background: rgba(139, 92, 246, 0.2);
      }
      100% {
        background: transparent;
      }
    }

    .processor-id {
      span {
        display: block;

        &:first-child {
          font-weight: 500;
          color: #fff;
        }
      }

      .event-types {
        font-size: 0.75rem;
        color: #666;
        margin-top: 0.25rem;
      }
    }

    .status-badge {
      display: inline-block;
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

    .mono {
      font-family: 'SF Mono', Monaco, monospace;
      font-size: 0.875rem;
    }

    .lag {
      &.warning {
        color: #f59e0b;
        font-weight: 500;
      }
    }

    .error {
      color: #ef4444;
      font-weight: 500;
    }

    .muted {
      color: #666;
    }

    .detail-panel {
      margin-top: 1.5rem;
      background: #1a1a1a;
      border: 1px solid #2a2a2a;
      border-radius: 8px;
      padding: 1.25rem;

      h3 {
        font-size: 1rem;
        font-weight: 600;
        color: #fff;
        margin-bottom: 1rem;
      }

      h4 {
        font-size: 0.75rem;
        text-transform: uppercase;
        letter-spacing: 0.05em;
        color: #666;
        margin-bottom: 0.5rem;
      }

      ul {
        list-style: none;
        display: flex;
        flex-wrap: wrap;
        gap: 0.5rem;
      }

      li {
        font-size: 0.8125rem;
        padding: 0.25rem 0.5rem;
        background: #252525;
        border-radius: 4px;
        font-family: 'SF Mono', Monaco, monospace;
      }
    }
  `,
})
export class ProcessorsComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly subscriptionService = inject(ProcessorSubscriptionService);
  private readonly destroyRef = inject(DestroyRef);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly processors = signal<ProcessorStatus[]>([]);
  readonly selectedProcessor = signal<ProcessorStatus | null>(null);
  readonly actionInProgress = signal(false);
  readonly subscriptionActive = signal(false);
  readonly usingPolling = signal(false);
  readonly recentlyUpdated = signal<Set<string>>(new Set());

  private subscriptions = new Subscription();
  private currentModuleKey: string | null = null;

  constructor() {
    // React to module key changes
    effect(() => {
      const moduleKey = this.api.moduleKey();
      if (this.currentModuleKey !== null && this.currentModuleKey !== moduleKey) {
        // Module changed - reload everything
        this.restartSubscription(moduleKey);
        this.loadData();
      }
      this.currentModuleKey = moduleKey;
    });

    // Clean up subscriptions on destroy
    this.destroyRef.onDestroy(() => {
      this.subscriptions.unsubscribe();
    });
  }

  ngOnInit(): void {
    this.loadData();
    this.startSubscription();
  }

  private restartSubscription(moduleKey: string): void {
    this.subscriptions.unsubscribe();
    this.subscriptions = new Subscription();
    this.subscriptionActive.set(false);
    this.startSubscriptionForModule(moduleKey);
  }

  loadData(): void {
    this.loading.set(true);
    this.error.set(null);

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

  private startSubscription(): void {
    this.startSubscriptionForModule(this.api.moduleKey());
  }

  private startSubscriptionForModule(moduleKey: string): void {
    this.subscriptions.add(
      this.subscriptionService
        .subscribeToProcessorStatus(moduleKey)
        .subscribe({
          next: (update) => {
            this.subscriptionActive.set(true);
            this.usingPolling.set(this.subscriptionService.usingPolling());
            this.updateProcessor(update.processor);
            this.highlightProcessor(update.processor.processorId);
          },
          error: (err) => {
            console.error('Subscription error:', err);
            this.subscriptionActive.set(false);
            // Retry after delay
            setTimeout(() => this.startSubscriptionForModule(moduleKey), 5000);
          },
        })
    );
  }

  private updateProcessor(updated: ProcessorStatus): void {
    const current = this.processors();
    const index = current.findIndex((p) => p.processorId === updated.processorId);

    if (index >= 0) {
      const newList = [...current];
      newList[index] = updated;
      this.processors.set(newList);
    }
  }

  private highlightProcessor(processorId: string): void {
    const updated = new Set(this.recentlyUpdated());
    updated.add(processorId);
    this.recentlyUpdated.set(updated);

    // Remove highlight after animation
    setTimeout(() => {
      const current = new Set(this.recentlyUpdated());
      current.delete(processorId);
      this.recentlyUpdated.set(current);
    }, 1000);
  }

  activate(processorId: string): void {
    this.actionInProgress.set(true);
    this.api.activateProcessor(processorId).subscribe({
      next: () => {
        this.loadData();
        this.actionInProgress.set(false);
      },
      error: () => {
        this.actionInProgress.set(false);
      },
    });
  }

  deactivate(processorId: string): void {
    this.actionInProgress.set(true);
    this.api.deactivateProcessor(processorId).subscribe({
      next: () => {
        this.loadData();
        this.actionInProgress.set(false);
      },
      error: () => {
        this.actionInProgress.set(false);
      },
    });
  }

  selectProcessor(processor: ProcessorStatus): void {
    this.selectedProcessor.set(processor);
  }
}
