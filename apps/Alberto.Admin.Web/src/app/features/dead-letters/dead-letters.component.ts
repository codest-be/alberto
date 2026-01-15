import { Component, inject, OnInit, signal, computed } from '@angular/core';
import { DatePipe, SlicePipe } from '@angular/common';
import { AdminApiService } from '../../core/services/admin-api.service';
import { DeadLetter, PagedResult } from '../../core/models/admin.models';

@Component({
  selector: 'app-dead-letters',
  imports: [DatePipe, SlicePipe],
  template: `
    <div class="dead-letters">
      <header class="page-header">
        <div>
          <h1>Dead Letters</h1>
          <p class="subtitle">Failed events that require attention</p>
        </div>
        <div class="header-actions">
          <span class="count-badge" [class.warning]="totalCount() > 0">
            {{ totalCount() }} total
          </span>
          <button class="btn btn-secondary" (click)="loadData()">Refresh</button>
        </div>
      </header>

      @if (loading()) {
        <div class="loading">Loading dead letters...</div>
      } @else if (error()) {
        <div class="error">
          <p>{{ error() }}</p>
          <button class="btn" (click)="loadData()">Retry</button>
        </div>
      } @else if (deadLetters().length === 0) {
        <div class="empty success">No dead letters - all events processed successfully</div>
      } @else {
        <div class="table-container">
          <table class="table">
            <thead>
              <tr>
                <th>Event Type</th>
                <th>Processor</th>
                <th>Error</th>
                <th>Attempts</th>
                <th>Failed At</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              @for (dl of deadLetters(); track dl.id) {
                <tr (click)="selectDeadLetter(dl)" [class.selected]="selectedId() === dl.id">
                  <td class="event-type">{{ dl.eventType }}</td>
                  <td class="processor">{{ dl.processorId }}</td>
                  <td class="error-msg">{{ dl.errorMessage | slice: 0 : 80 }}...</td>
                  <td class="attempts">{{ dl.attemptCount }}</td>
                  <td class="muted">{{ dl.failedAt | date: 'short' }}</td>
                  <td class="actions" (click)="$event.stopPropagation()">
                    <button
                      class="btn btn-sm btn-danger"
                      (click)="confirmRemove(dl)"
                      [disabled]="actionInProgress()"
                    >
                      Remove
                    </button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        @if (totalPages() > 1) {
          <div class="pagination">
            <button class="btn btn-sm" [disabled]="page() === 1" (click)="goToPage(page() - 1)">
              Previous
            </button>
            <span class="page-info">Page {{ page() }} of {{ totalPages() }}</span>
            <button
              class="btn btn-sm"
              [disabled]="page() === totalPages()"
              (click)="goToPage(page() + 1)"
            >
              Next
            </button>
          </div>
        }
      }

      @if (selectedDeadLetter()) {
        <div class="detail-panel">
          <div class="detail-header">
            <h3>Dead Letter Details</h3>
            <button class="btn btn-sm" (click)="selectedDeadLetter.set(null)">Close</button>
          </div>

          <div class="detail-grid">
            <div class="detail-item">
              <span class="label">Event ID</span>
              <span class="value mono">{{ selectedDeadLetter()?.eventId }}</span>
            </div>
            <div class="detail-item">
              <span class="label">Event Type</span>
              <span class="value">{{ selectedDeadLetter()?.eventType }}</span>
            </div>
            <div class="detail-item">
              <span class="label">Processor</span>
              <span class="value">{{ selectedDeadLetter()?.processorId }}</span>
            </div>
            <div class="detail-item">
              <span class="label">Attempts</span>
              <span class="value">{{ selectedDeadLetter()?.attemptCount }}</span>
            </div>
            <div class="detail-item">
              <span class="label">Failed At</span>
              <span class="value">{{ selectedDeadLetter()?.failedAt | date: 'medium' }}</span>
            </div>
          </div>

          <div class="detail-section">
            <h4>Error Message</h4>
            <pre class="error-block">{{ selectedDeadLetter()?.errorMessage }}</pre>
          </div>

          @if (selectedDeadLetter()?.stackTrace) {
            <div class="detail-section">
              <h4>Stack Trace</h4>
              <pre class="stack-trace">{{ selectedDeadLetter()?.stackTrace }}</pre>
            </div>
          }

          <div class="detail-section">
            <h4>Event Data</h4>
            <pre class="json-block">{{ formatJson(selectedDeadLetter()?.eventData) }}</pre>
          </div>
        </div>
      }

      @if (confirmingRemove()) {
        <div class="modal-overlay" (click)="closeModal()">
          <div class="modal" (click)="$event.stopPropagation()">
            <h3>Remove Dead Letter</h3>
            <p class="warning-text">Are you sure you want to remove this dead letter?</p>
            <p class="warning-detail">
              Event: <strong>{{ confirmingRemove()?.eventType }}</strong>
            </p>
            <div class="modal-actions">
              <button class="btn" (click)="closeModal()">Cancel</button>
              <button class="btn btn-danger" (click)="removeDeadLetter()">Remove</button>
            </div>
          </div>
        </div>
      }
    </div>
  `,
  styles: `
    .dead-letters {
      max-width: 1400px;
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

    .header-actions {
      display: flex;
      align-items: center;
      gap: 1rem;
    }

    .count-badge {
      padding: 0.375rem 0.75rem;
      background: #252525;
      border-radius: 6px;
      font-size: 0.875rem;
      color: #888;

      &.warning {
        background: rgba(245, 158, 11, 0.15);
        color: #f59e0b;
      }
    }

    .loading,
    .error,
    .empty {
      padding: 2rem;
      text-align: center;
      background: #1a1a1a;
      border-radius: 8px;
    }

    .empty.success {
      color: #22c55e;
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

      tbody tr {
        cursor: pointer;
        transition: background 0.15s;

        &:hover {
          background: #1f1f1f;
        }

        &.selected {
          background: #252525;
        }
      }
    }

    .event-type {
      font-family: 'SF Mono', Monaco, monospace;
      font-size: 0.8125rem;
      color: #8b5cf6;
    }

    .processor {
      font-weight: 500;
      color: #e0e0e0;
    }

    .error-msg {
      font-size: 0.8125rem;
      color: #ef4444;
      max-width: 300px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .attempts {
      font-weight: 500;
      color: #f59e0b;
    }

    .muted {
      color: #666;
    }

    .actions {
      white-space: nowrap;
    }

    .pagination {
      display: flex;
      justify-content: center;
      align-items: center;
      gap: 1rem;
      margin-top: 1rem;
    }

    .page-info {
      font-size: 0.875rem;
      color: #666;
    }

    .detail-panel {
      margin-top: 1.5rem;
      background: #1a1a1a;
      border: 1px solid #2a2a2a;
      border-radius: 8px;
      padding: 1.25rem;
    }

    .detail-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 1rem;

      h3 {
        font-size: 1rem;
        font-weight: 600;
        color: #fff;
      }
    }

    .detail-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 1rem;
      margin-bottom: 1.25rem;
    }

    .detail-item {
      .label {
        display: block;
        font-size: 0.75rem;
        text-transform: uppercase;
        letter-spacing: 0.05em;
        color: #666;
        margin-bottom: 0.25rem;
      }

      .value {
        color: #e0e0e0;

        &.mono {
          font-family: 'SF Mono', Monaco, monospace;
          font-size: 0.8125rem;
        }
      }
    }

    .detail-section {
      margin-top: 1.25rem;

      h4 {
        font-size: 0.75rem;
        text-transform: uppercase;
        letter-spacing: 0.05em;
        color: #666;
        margin-bottom: 0.5rem;
      }
    }

    .error-block,
    .stack-trace,
    .json-block {
      background: #151515;
      border: 1px solid #252525;
      border-radius: 6px;
      padding: 1rem;
      font-family: 'SF Mono', Monaco, monospace;
      font-size: 0.8125rem;
      overflow-x: auto;
      white-space: pre-wrap;
      word-break: break-word;
    }

    .error-block {
      color: #ef4444;
    }

    .stack-trace {
      color: #888;
      max-height: 200px;
      overflow-y: auto;
    }

    .json-block {
      color: #e0e0e0;
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
        margin-bottom: 0.75rem;
      }
    }

    .warning-text {
      color: #e0e0e0;
      margin-bottom: 0.5rem;
    }

    .warning-detail {
      color: #666;
      font-size: 0.875rem;
      margin-bottom: 1.25rem;

      strong {
        color: #8b5cf6;
        font-family: 'SF Mono', Monaco, monospace;
      }
    }

    .modal-actions {
      display: flex;
      justify-content: flex-end;
      gap: 0.75rem;
    }
  `,
})
export class DeadLettersComponent implements OnInit {
  private readonly api = inject(AdminApiService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly pagedResult = signal<PagedResult<DeadLetter> | null>(null);
  readonly selectedDeadLetter = signal<DeadLetter | null>(null);
  readonly confirmingRemove = signal<DeadLetter | null>(null);
  readonly actionInProgress = signal(false);

  readonly deadLetters = computed(() => this.pagedResult()?.items ?? []);
  readonly totalCount = computed(() => this.pagedResult()?.totalCount ?? 0);
  readonly page = computed(() => this.pagedResult()?.page ?? 1);
  readonly totalPages = computed(() => this.pagedResult()?.totalPages ?? 1);
  readonly selectedId = computed(() => this.selectedDeadLetter()?.id ?? null);

  ngOnInit(): void {
    this.loadData();
  }

  loadData(page: number = 1): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.getDeadLetters(undefined, page, 20).subscribe({
      next: (result) => {
        this.pagedResult.set(result);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err.message || 'Failed to load dead letters');
        this.loading.set(false);
      },
    });
  }

  goToPage(page: number): void {
    this.loadData(page);
  }

  selectDeadLetter(dl: DeadLetter): void {
    this.selectedDeadLetter.set(dl);
  }

  confirmRemove(dl: DeadLetter): void {
    this.confirmingRemove.set(dl);
  }

  closeModal(): void {
    this.confirmingRemove.set(null);
  }

  removeDeadLetter(): void {
    const dl = this.confirmingRemove();
    if (!dl) return;

    this.actionInProgress.set(true);
    this.api.removeDeadLetter(dl.id).subscribe({
      next: () => {
        this.closeModal();
        if (this.selectedDeadLetter()?.id === dl.id) {
          this.selectedDeadLetter.set(null);
        }
        this.loadData(this.page());
        this.actionInProgress.set(false);
      },
      error: () => {
        this.actionInProgress.set(false);
      },
    });
  }

  formatJson(json: string | undefined): string {
    if (!json) return '';
    try {
      return JSON.stringify(JSON.parse(json), null, 2);
    } catch {
      return json;
    }
  }
}
