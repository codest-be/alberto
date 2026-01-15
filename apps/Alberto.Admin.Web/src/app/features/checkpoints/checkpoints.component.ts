import { Component, inject, OnInit, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AdminApiService } from '../../core/services/admin-api.service';
import { Checkpoint } from '../../core/models/admin.models';

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
        <button class="btn btn-secondary" (click)="loadData()">Refresh</button>
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
                <th>Processor ID</th>
                <th>Last Position</th>
                <th>Updated At</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              @for (checkpoint of checkpoints(); track checkpoint.processorId) {
                <tr>
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
    </div>
  `,
  styles: `
    .checkpoints {
      max-width: 1000px;
    }

    .page-header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
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
  `,
})
export class CheckpointsComponent implements OnInit {
  private readonly api = inject(AdminApiService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly checkpoints = signal<Checkpoint[]>([]);
  readonly actionInProgress = signal(false);
  readonly editingCheckpoint = signal<Checkpoint | null>(null);
  readonly confirmingReset = signal<string | null>(null);

  newPosition: number | null = null;

  ngOnInit(): void {
    this.loadData();
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
    this.newPosition = null;
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
