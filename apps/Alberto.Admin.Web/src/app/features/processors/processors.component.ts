import { Component, inject, OnInit, signal, DestroyRef, effect } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription, interval } from 'rxjs';
import { AdminApiService } from '../../core/services/admin-api.service';
import { ProcessorSubscriptionService } from '../../core/graphql/processor-subscription.service';
import { ProcessorStatus, RebuildStatus } from '../../core/models/admin.models';

@Component({
  selector: 'app-processors',
  imports: [DatePipe, DecimalPipe],
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
                  [class.rebuilding]="processor.isRebuilding"
                  [class.updated]="recentlyUpdated().has(processor.processorId)"
                >
                  <td class="processor-id">
                    <span>{{ processor.processorId }}</span>
                    <span class="event-types">
                      {{ processor.handledEventTypes.length }} event type(s)
                    </span>
                  </td>
                  <td>
                    @if (rebuildStatuses().get(processor.processorId); as status) {
                      @if (status.state === 'Rebuilding') {
                        <span class="status-badge rebuilding">
                          Rebuilding v{{ status.rebuildingVersion }}
                          ({{ status.progressPercent | number:'1.0-0' }}%)
                        </span>
                      } @else if (status.state === 'ReadyToSwap') {
                        <span class="status-badge ready-to-swap">
                          Ready to Swap v{{ status.rebuildingVersion }}
                        </span>
                      } @else if (status.state === 'Swapped') {
                        <span class="status-badge swapped">
                          Swapped to v{{ status.activeVersion }}
                        </span>
                      } @else {
                        <span class="status-badge" [class.active]="processor.isActive">
                          {{ processor.isActive ? 'Active' : 'Inactive' }}
                          @if (status.activeVersion && status.activeVersion > 1) {
                            (v{{ status.activeVersion }})
                          }
                        </span>
                      }
                    } @else if (processor.isRebuilding) {
                      <span class="status-badge rebuilding">
                        Rebuilding
                      </span>
                    } @else {
                      <span class="status-badge" [class.active]="processor.isActive">
                        {{ processor.isActive ? 'Active' : 'Inactive' }}
                      </span>
                    }
                  </td>
                  <td class="mono">{{ processor.lastPosition ?? '-' }}</td>
                  <td class="mono">{{ processor.globalPosition }}</td>
                  <td>
                    <span class="lag" [class.warning]="processor.lag > 100" [class.rebuilding]="processor.isRebuilding">
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
                  <td class="actions">
                    @if (rebuildStatuses().get(processor.processorId); as status) {
                      @if (status.state === 'Rebuilding') {
                        <button
                          class="btn btn-sm btn-danger"
                          (click)="rollbackRebuild(processor.processorId)"
                          [disabled]="actionInProgress()"
                          title="Cancel and rollback rebuild"
                        >
                          Rollback
                        </button>
                      } @else if (status.state === 'ReadyToSwap') {
                        <button
                          class="btn btn-sm btn-success"
                          (click)="swapVersion(processor.processorId)"
                          [disabled]="actionInProgress()"
                          title="Swap to rebuilt version"
                        >
                          Swap
                        </button>
                        <button
                          class="btn btn-sm btn-danger"
                          (click)="rollbackRebuild(processor.processorId)"
                          [disabled]="actionInProgress()"
                          title="Rollback rebuild"
                        >
                          Rollback
                        </button>
                      } @else if (status.state === 'Swapped' && status.activeVersion && status.activeVersion > 1) {
                        <button
                          class="btn btn-sm btn-secondary"
                          (click)="cleanupOldVersion(processor.processorId, status.activeVersion - 1)"
                          [disabled]="actionInProgress()"
                          title="Delete old version data"
                        >
                          Cleanup v{{ status.activeVersion - 1 }}
                        </button>
                        <button
                          class="btn btn-sm btn-secondary"
                          (click)="startVersionedRebuild(processor.processorId)"
                          [disabled]="actionInProgress()"
                          title="Zero-downtime rebuild"
                        >
                          Rebuild
                        </button>
                      } @else {
                        <button
                          class="btn btn-sm btn-secondary"
                          (click)="startVersionedRebuild(processor.processorId)"
                          [disabled]="actionInProgress()"
                          title="Zero-downtime rebuild"
                        >
                          Rebuild
                        </button>
                      }
                    } @else if (processor.isRebuilding) {
                      <button
                        class="btn btn-sm btn-danger"
                        (click)="cancelRebuild(processor.processorId)"
                        [disabled]="actionInProgress()"
                      >
                        Cancel
                      </button>
                    } @else {
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
                      <button
                        class="btn btn-sm btn-secondary"
                        (click)="startVersionedRebuild(processor.processorId)"
                        [disabled]="actionInProgress()"
                        title="Zero-downtime rebuild"
                      >
                        Rebuild
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

      &.btn-danger {
        background: #dc2626;
        border-color: #dc2626;
        color: #fff;

        &:hover:not(:disabled) {
          background: #b91c1c;
        }
      }

      &.btn-success {
        background: #22c55e;
        border-color: #22c55e;
        color: #fff;

        &:hover:not(:disabled) {
          background: #16a34a;
        }
      }
    }

    .actions {
      display: flex;
      gap: 0.5rem;
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

      tr.rebuilding {
        background: rgba(245, 158, 11, 0.05);
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

      &.rebuilding {
        background: rgba(245, 158, 11, 0.15);
        color: #f59e0b;
        animation: pulse 2s infinite;
      }

      &.ready-to-swap {
        background: rgba(34, 197, 94, 0.15);
        color: #22c55e;
        animation: pulse 2s infinite;
      }

      &.swapped {
        background: rgba(99, 102, 241, 0.15);
        color: #6366f1;
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

      &.rebuilding {
        color: #f59e0b;
        font-style: italic;
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
  readonly rebuildStatuses = signal<Map<string, RebuildStatus>>(new Map());

  private subscriptions = new Subscription();
  private currentModuleKey: string | null = null;
  private rebuildPollingSubscription: Subscription | null = null;

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
      this.rebuildPollingSubscription?.unsubscribe();
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

  startRebuild(processorId: string): void {
    if (!confirm(`Are you sure you want to rebuild ${processorId}? This will clear all projection state and reprocess from the beginning.`)) {
      return;
    }

    this.actionInProgress.set(true);
    this.api.startRebuild(processorId, true).subscribe({
      next: (status) => {
        // Update rebuild status
        const statuses = new Map(this.rebuildStatuses());
        statuses.set(processorId, status);
        this.rebuildStatuses.set(statuses);

        // Reload processors to get isRebuilding state
        this.loadData();
        this.actionInProgress.set(false);

        // Start polling for rebuild status
        this.startRebuildStatusPolling(processorId);
      },
      error: () => {
        this.actionInProgress.set(false);
      },
    });
  }

  cancelRebuild(processorId: string): void {
    this.actionInProgress.set(true);
    this.api.cancelRebuild(processorId).subscribe({
      next: () => {
        // Remove from rebuild statuses
        const statuses = new Map(this.rebuildStatuses());
        statuses.delete(processorId);
        this.rebuildStatuses.set(statuses);

        this.loadData();
        this.actionInProgress.set(false);
      },
      error: () => {
        this.actionInProgress.set(false);
      },
    });
  }

  private startRebuildStatusPolling(processorId: string): void {
    // Poll every 2 seconds for rebuild status
    this.rebuildPollingSubscription?.unsubscribe();
    this.rebuildPollingSubscription = interval(2000).subscribe(() => {
      this.api.getRebuildStatus(processorId).subscribe({
        next: (status) => {
          if (status) {
            const statuses = new Map(this.rebuildStatuses());
            statuses.set(processorId, status);
            this.rebuildStatuses.set(statuses);

            // Stop polling if rebuild reached a terminal state (keep polling for ReadyToSwap)
            if (status.state === 'Completed' || status.state === 'Failed' ||
                status.state === 'Cancelled' || status.state === 'RolledBack') {
              this.rebuildPollingSubscription?.unsubscribe();
              this.rebuildPollingSubscription = null;
              this.loadData();
            }
          }
        },
      });
    });
  }

  // Versioned Rebuild Operations (Zero-downtime)
  startVersionedRebuild(processorId: string): void {
    if (!confirm(`Start zero-downtime rebuild for ${processorId}?\n\nThis creates a new version while keeping the current version active. Queries continue working during the rebuild.`)) {
      return;
    }

    this.actionInProgress.set(true);
    this.api.startVersionedRebuild(processorId).subscribe({
      next: (status) => {
        const statuses = new Map(this.rebuildStatuses());
        statuses.set(processorId, status);
        this.rebuildStatuses.set(statuses);

        this.loadData();
        this.actionInProgress.set(false);

        // Start polling for rebuild status
        this.startRebuildStatusPolling(processorId);
      },
      error: (err) => {
        alert(`Failed to start rebuild: ${err.message || 'Unknown error'}`);
        this.actionInProgress.set(false);
      },
    });
  }

  swapVersion(processorId: string): void {
    if (!confirm(`Swap to the rebuilt version for ${processorId}?\n\nQueries will immediately start reading from the new version.`)) {
      return;
    }

    this.actionInProgress.set(true);
    this.api.swapRebuildVersion(processorId).subscribe({
      next: (status) => {
        const statuses = new Map(this.rebuildStatuses());
        statuses.set(processorId, status);
        this.rebuildStatuses.set(statuses);

        this.loadData();
        this.actionInProgress.set(false);
      },
      error: (err) => {
        alert(`Failed to swap version: ${err.message || 'Unknown error'}`);
        this.actionInProgress.set(false);
      },
    });
  }

  rollbackRebuild(processorId: string): void {
    if (!confirm(`Rollback the rebuild for ${processorId}?\n\nThis will discard the rebuilding version and keep the current active version.`)) {
      return;
    }

    this.actionInProgress.set(true);
    this.api.rollbackRebuild(processorId).subscribe({
      next: (status) => {
        const statuses = new Map(this.rebuildStatuses());
        statuses.set(processorId, status);
        this.rebuildStatuses.set(statuses);

        // Stop polling
        this.rebuildPollingSubscription?.unsubscribe();
        this.rebuildPollingSubscription = null;

        this.loadData();
        this.actionInProgress.set(false);
      },
      error: (err) => {
        alert(`Failed to rollback rebuild: ${err.message || 'Unknown error'}`);
        this.actionInProgress.set(false);
      },
    });
  }

  cleanupOldVersion(processorId: string, version: number): void {
    if (!confirm(`Delete old version ${version} data for ${processorId}?\n\nThis will free up storage. This action cannot be undone.`)) {
      return;
    }

    this.actionInProgress.set(true);
    this.api.cleanupOldVersion(processorId, version).subscribe({
      next: () => {
        // Clear the rebuild status since cleanup is complete
        const statuses = new Map(this.rebuildStatuses());
        statuses.delete(processorId);
        this.rebuildStatuses.set(statuses);

        this.loadData();
        this.actionInProgress.set(false);
      },
      error: (err) => {
        alert(`Failed to cleanup old version: ${err.message || 'Unknown error'}`);
        this.actionInProgress.set(false);
      },
    });
  }

  selectProcessor(processor: ProcessorStatus): void {
    this.selectedProcessor.set(processor);
  }
}
