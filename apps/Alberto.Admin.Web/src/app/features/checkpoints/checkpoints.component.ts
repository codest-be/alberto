import { Component, inject, OnInit, signal, computed, DestroyRef, effect } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription } from 'rxjs';
import { AdminApiService } from '../../core/services/admin-api.service';
import { AdminSubscriptionService } from '../../core/graphql/admin-subscription.service';
import { Checkpoint, BulkOperationResult } from '../../core/models/admin.models';

@Component({
  selector: 'app-checkpoints',
  imports: [DatePipe, FormsModule],
  template: `
    <div class="checkpoints">
      <header class="page-header">
        <div>
          <h1>Checkpoints</h1>
          <p class="subtitle">View and manage processor checkpoint positions</p>
        </div>
        <div class="header-actions">
          @if (selectedCount() > 0) {
            <span class="selection-badge">{{ selectedCount() }} selected</span>
            <button
              class="btn btn-danger"
              (click)="confirmBulkReset()"
              [disabled]="actionInProgress()"
            >
              Reset Selected
            </button>
            <button class="btn btn-secondary" (click)="clearSelection()">Clear</button>
          }
          <span class="live-indicator" [class.connected]="subscriptionService.connected()">
            <span class="live-dot"></span>
            {{ subscriptionService.connected() ? 'Live' : 'Connecting...' }}
          </span>
          <button class="btn btn-secondary" (click)="loadData()">Refresh</button>
        </div>
      </header>

      @if (loading()) {
        <div class="loading">Loading checkpoints...</div>
      } @else if (error()) {
        <div class="error">
          <p>{{ error() }}</p>
          <button class="btn" (click)="loadData()">Retry</button>
        </div>
      } @else if (checkpoints().length === 0) {
        <div class="empty">No checkpoints found</div>
      } @else {
        <div class="table-container">
          <table class="table">
            <thead>
              <tr>
                <th class="checkbox-col">
                  <input
                    type="checkbox"
                    [checked]="allSelected()"
                    (change)="toggleSelectAll()"
                  />
                </th>
                <th>Processor ID</th>
                <th>Last Position</th>
                <th>Updated At</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              @for (checkpoint of checkpoints(); track checkpoint.processorId) {
                <tr
                  [class.updated]="recentlyUpdated().has(checkpoint.processorId)"
                  [class.selected]="selected().has(checkpoint.processorId)"
                >
                  <td class="checkbox-col">
                    <input
                      type="checkbox"
                      [checked]="selected().has(checkpoint.processorId)"
                      (change)="toggleSelect(checkpoint.processorId)"
                    />
                  </td>
                  <td class="processor-id">{{ checkpoint.processorId }}</td>
                  <td class="mono">{{ checkpoint.lastPosition }}</td>
                  <td class="muted">{{ checkpoint.updatedAt | date: 'medium' }}</td>
                  <td class="actions">
                    <button
                      class="btn btn-sm"
                      (click)="openSetPosition(checkpoint)"
                      [disabled]="actionInProgress()"
                    >
                      Set Position
                    </button>
                    <button
                      class="btn btn-sm btn-danger"
                      (click)="confirmReset(checkpoint.processorId)"
                      [disabled]="actionInProgress()"
                    >
                      Reset
                    </button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      @if (editingCheckpoint()) {
        <div class="modal-overlay" (click)="closeModal()">
          <div class="modal" (click)="$event.stopPropagation()">
            <h3>Set Checkpoint Position</h3>
            <p class="modal-processor">{{ editingCheckpoint()?.processorId }}</p>
            <div class="form-group">
              <label for="position">New Position</label>
              <input
                id="position"
                type="number"
                class="input"
                [(ngModel)]="newPosition"
                [placeholder]="editingCheckpoint()?.lastPosition?.toString()"
              />
            </div>
            <div class="modal-actions">
              <button class="btn" (click)="closeModal()">Cancel</button>
              <button
                class="btn btn-primary"
                (click)="setPosition()"
                [disabled]="newPosition === null"
              >
                Save
              </button>
            </div>
          </div>
        </div>
      }

      @if (confirmingReset()) {
        <div class="modal-overlay" (click)="closeModal()">
          <div class="modal" (click)="$event.stopPropagation()">
            <h3>Reset Checkpoint</h3>
            <p class="warning-text">
              Are you sure you want to reset the checkpoint for
              <strong>{{ confirmingReset() }}</strong
              >?
            </p>
            <p class="warning-detail">
              This will delete the checkpoint and cause the processor to start from the beginning.
            </p>
            <div class="modal-actions">
              <button class="btn" (click)="closeModal()">Cancel</button>
              <button class="btn btn-danger" (click)="resetCheckpoint()">Reset</button>
            </div>
          </div>
        </div>
      }

      @if (showBulkResetModal()) {
        <div class="modal-overlay" (click)="closeModal()">
          <div class="modal" (click)="$event.stopPropagation()">
            <h3>Reset Multiple Checkpoints</h3>
            <p class="warning-text">
              Are you sure you want to reset {{ selectedCount() }} checkpoints?
            </p>
            <div class="selected-list">
              @for (id of Array.from(selected()); track id) {
                <span class="selected-item">{{ id }}</span>
              }
            </div>
            <p class="warning-detail">
              This will delete these checkpoints and cause the processors to start from the beginning.
            </p>
            @if (bulkResetResult()) {
              <div class="bulk-result">
                <p class="success-count">Success: {{ bulkResetResult()?.successCount }}</p>
                <p class="fail-count">Failed: {{ bulkResetResult()?.failCount }}</p>
              </div>
            }
            <div class="modal-actions">
              <button class="btn" (click)="closeModal()">{{ bulkResetResult() ? 'Close' : 'Cancel' }}</button>
              @if (!bulkResetResult()) {
                <button class="btn btn-danger" (click)="bulkResetCheckpoints()" [disabled]="actionInProgress()">
                  {{ actionInProgress() ? 'Resetting...' : 'Reset All' }}
                </button>
              }
            </div>
          </div>
        </div>
      }
    </div>
  `,
  styles: `
    .checkpoints {
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
      flex-wrap: wrap;
    }

    .selection-badge {
      padding: 0.375rem 0.75rem;
      background: rgba(99, 102, 241, 0.15);
      color: #6366f1;
      border-radius: 6px;
      font-size: 0.875rem;
    }

    .checkbox-col {
      width: 40px;
      text-align: center;
    }

    .checkbox-col input[type="checkbox"] {
      width: 16px;
      height: 16px;
      cursor: pointer;
    }

    tr.selected {
      background: rgba(99, 102, 241, 0.1);
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
    }

    @keyframes pulse {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.5; }
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

      &.btn-danger {
        background: transparent;
        border-color: #dc2626;
        color: #dc2626;

        &:hover:not(:disabled) {
          background: rgba(220, 38, 38, 0.1);
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

      tr.updated {
        animation: highlight 1s ease-out;
      }
    }

    @keyframes highlight {
      0% { background: rgba(139, 92, 246, 0.2); }
      100% { background: transparent; }
    }

    .processor-id {
      font-weight: 500;
      color: #fff;
    }

    .mono {
      font-family: 'SF Mono', Monaco, monospace;
      font-size: 0.875rem;
    }

    .muted {
      color: #666;
    }

    .actions {
      display: flex;
      gap: 0.5rem;
    }

    .modal-overlay {
      position: fixed;
      inset: 0;
      background: rgba(0, 0, 0, 0.7);
      display: flex;
      align-items: center;
      justify-content: center;
      z-index: 1000;
    }

    .modal {
      background: #1a1a1a;
      border: 1px solid #2a2a2a;
      border-radius: 12px;
      padding: 1.5rem;
      width: 100%;
      max-width: 400px;

      h3 {
        font-size: 1.125rem;
        font-weight: 600;
        color: #fff;
        margin-bottom: 0.5rem;
      }
    }

    .modal-processor {
      font-family: 'SF Mono', Monaco, monospace;
      font-size: 0.875rem;
      color: #8b5cf6;
      margin-bottom: 1.25rem;
    }

    .form-group {
      margin-bottom: 1.25rem;

      label {
        display: block;
        font-size: 0.75rem;
        text-transform: uppercase;
        letter-spacing: 0.05em;
        color: #666;
        margin-bottom: 0.5rem;
      }
    }

    .input {
      width: 100%;
      padding: 0.625rem 0.875rem;
      background: #252525;
      border: 1px solid #333;
      border-radius: 6px;
      color: #e0e0e0;
      font-size: 0.875rem;
      font-family: 'SF Mono', Monaco, monospace;

      &:focus {
        outline: none;
        border-color: #6366f1;
      }
    }

    .modal-actions {
      display: flex;
      justify-content: flex-end;
      gap: 0.75rem;
    }

    .warning-text {
      color: #e0e0e0;
      margin-bottom: 0.5rem;

      strong {
        color: #fff;
      }
    }

    .warning-detail {
      color: #666;
      font-size: 0.875rem;
      margin-bottom: 1.25rem;
    }

    .selected-list {
      display: flex;
      flex-wrap: wrap;
      gap: 0.5rem;
      margin-bottom: 1rem;
      max-height: 150px;
      overflow-y: auto;
    }

    .selected-item {
      padding: 0.25rem 0.5rem;
      background: #252525;
      border-radius: 4px;
      font-family: 'SF Mono', Monaco, monospace;
      font-size: 0.75rem;
      color: #8b5cf6;
    }

    .bulk-result {
      background: #252525;
      border-radius: 6px;
      padding: 0.75rem;
      margin-bottom: 1rem;

      .success-count {
        color: #22c55e;
        margin-bottom: 0.25rem;
      }

      .fail-count {
        color: #ef4444;
      }
    }
  `,
})
export class CheckpointsComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly destroyRef = inject(DestroyRef);
  readonly subscriptionService = inject(AdminSubscriptionService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly checkpoints = signal<Checkpoint[]>([]);
  readonly actionInProgress = signal(false);
  readonly editingCheckpoint = signal<Checkpoint | null>(null);
  readonly confirmingReset = signal<string | null>(null);
  readonly recentlyUpdated = signal<Set<string>>(new Set());
  readonly selected = signal<Set<string>>(new Set());
  readonly showBulkResetModal = signal(false);
  readonly bulkResetResult = signal<BulkOperationResult | null>(null);

  readonly selectedCount = computed(() => this.selected().size);
  readonly allSelected = computed(() =>
    this.checkpoints().length > 0 && this.selected().size === this.checkpoints().length
  );

  readonly Array = Array;

  newPosition: number | null = null;

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
        this.clearSelection();
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
    this.startSubscriptionForModule(moduleKey);
  }

  private startSubscription(): void {
    this.startSubscriptionForModule(this.api.moduleKey());
  }

  private startSubscriptionForModule(moduleKey: string): void {
    this.subscriptions.add(
      this.subscriptionService
        .subscribeToCheckpoints(moduleKey)
        .subscribe({
          next: (update) => {
            this.updateCheckpoint(update.checkpoint);
            this.highlightCheckpoint(update.checkpoint.processorId);
          },
        })
    );
  }

  private updateCheckpoint(updated: Checkpoint): void {
    const current = this.checkpoints();
    const index = current.findIndex(c => c.processorId === updated.processorId);
    if (index >= 0) {
      const newList = [...current];
      newList[index] = updated;
      this.checkpoints.set(newList);
    }
  }

  private highlightCheckpoint(processorId: string): void {
    const updated = new Set(this.recentlyUpdated());
    updated.add(processorId);
    this.recentlyUpdated.set(updated);

    setTimeout(() => {
      const current = new Set(this.recentlyUpdated());
      current.delete(processorId);
      this.recentlyUpdated.set(current);
    }, 1000);
  }

  loadData(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.getCheckpoints().subscribe({
      next: (checkpoints) => {
        this.checkpoints.set(checkpoints);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err.message || 'Failed to load checkpoints');
        this.loading.set(false);
      },
    });
  }

  openSetPosition(checkpoint: Checkpoint): void {
    this.editingCheckpoint.set(checkpoint);
    this.newPosition = checkpoint.lastPosition;
  }

  confirmReset(processorId: string): void {
    this.confirmingReset.set(processorId);
  }

  closeModal(): void {
    this.editingCheckpoint.set(null);
    this.confirmingReset.set(null);
    this.showBulkResetModal.set(false);
    this.bulkResetResult.set(null);
    this.newPosition = null;
  }

  toggleSelect(processorId: string): void {
    const current = new Set(this.selected());
    if (current.has(processorId)) {
      current.delete(processorId);
    } else {
      current.add(processorId);
    }
    this.selected.set(current);
  }

  toggleSelectAll(): void {
    if (this.allSelected()) {
      this.selected.set(new Set());
    } else {
      this.selected.set(new Set(this.checkpoints().map(c => c.processorId)));
    }
  }

  clearSelection(): void {
    this.selected.set(new Set());
  }

  confirmBulkReset(): void {
    this.bulkResetResult.set(null);
    this.showBulkResetModal.set(true);
  }

  bulkResetCheckpoints(): void {
    const processorIds = Array.from(this.selected());
    if (processorIds.length === 0) return;

    this.actionInProgress.set(true);
    this.api.resetMultipleCheckpoints(processorIds).subscribe({
      next: (result) => {
        this.bulkResetResult.set(result);
        this.clearSelection();
        this.loadData();
        this.actionInProgress.set(false);
      },
      error: () => {
        this.actionInProgress.set(false);
      },
    });
  }

  setPosition(): void {
    const checkpoint = this.editingCheckpoint();
    if (!checkpoint || this.newPosition === null) return;

    this.actionInProgress.set(true);
    this.api.setCheckpoint(checkpoint.processorId, this.newPosition).subscribe({
      next: () => {
        this.closeModal();
        this.loadData();
        this.actionInProgress.set(false);
      },
      error: () => {
        this.actionInProgress.set(false);
      },
    });
  }

  resetCheckpoint(): void {
    const processorId = this.confirmingReset();
    if (!processorId) return;

    this.actionInProgress.set(true);
    this.api.resetCheckpoint(processorId).subscribe({
      next: () => {
        this.closeModal();
        this.loadData();
        this.actionInProgress.set(false);
      },
      error: () => {
        this.actionInProgress.set(false);
      },
    });
  }
}
