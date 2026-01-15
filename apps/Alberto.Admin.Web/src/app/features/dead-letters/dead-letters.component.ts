import { Component, inject, OnInit, OnDestroy, signal, computed } from '@angular/core';
import { DatePipe, SlicePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription, Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { AdminApiService } from '../../core/services/admin-api.service';
import { AdminSubscriptionService } from '../../core/graphql/admin-subscription.service';
import { DeadLetter, DeadLetterFilter, PagedResult, BulkRetryResult } from '../../core/models/admin.models';

@Component({
  selector: 'app-dead-letters',
  imports: [DatePipe, SlicePipe, FormsModule],
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
          @if (filterProcessorId()) {
            <button
              class="btn btn-primary"
              (click)="confirmRetryAll()"
              [disabled]="actionInProgress() || totalCount() === 0"
            >
              Retry All ({{ totalCount() }})
            </button>
          }
          <span class="live-indicator" [class.connected]="subscriptionService.connected()">
            <span class="live-dot"></span>
            {{ subscriptionService.connected() ? 'Live' : 'Connecting...' }}
          </span>
          <button class="btn btn-secondary" (click)="loadData()">Refresh</button>
        </div>
      </header>

      <div class="filter-bar">
        <div class="filter-group">
          <label>Processor</label>
          <select [value]="filterProcessorId() || ''" (change)="onProcessorFilterChange($event)">
            <option value="">All Processors</option>
            @for (p of processors(); track p) {
              <option [value]="p">{{ p }}</option>
            }
          </select>
        </div>

        <div class="filter-group">
          <label>Event Type</label>
          <select [value]="filterEventType() || ''" (change)="onEventTypeFilterChange($event)">
            <option value="">All Types</option>
            @for (type of eventTypes(); track type) {
              <option [value]="type">{{ type }}</option>
            }
          </select>
        </div>

        <div class="filter-group search-group">
          <label>Search Error</label>
          <input
            type="text"
            placeholder="Search error messages..."
            [ngModel]="filterSearchTerm()"
            (ngModelChange)="onSearchTermChange($event)"
          />
        </div>

        <div class="filter-group">
          <label>Failed After</label>
          <input
            type="datetime-local"
            [ngModel]="filterFailedAfter()"
            (ngModelChange)="onFailedAfterChange($event)"
          />
        </div>

        <div class="filter-group">
          <label>Failed Before</label>
          <input
            type="datetime-local"
            [ngModel]="filterFailedBefore()"
            (ngModelChange)="onFailedBeforeChange($event)"
          />
        </div>

        @if (hasActiveFilters()) {
          <button class="btn btn-clear" (click)="clearFilters()">
            Clear Filters
          </button>
        }
      </div>

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
                <tr
                  (click)="selectDeadLetter(dl)"
                  [class.selected]="selectedId() === dl.id"
                  [class.new-item]="recentlyAdded().has(dl.id)"
                >
                  <td class="event-type">{{ dl.eventType }}</td>
                  <td class="processor">{{ dl.processorId }}</td>
                  <td class="error-msg">{{ dl.errorMessage | slice: 0 : 80 }}...</td>
                  <td class="attempts">{{ dl.attemptCount }}</td>
                  <td class="muted">{{ dl.failedAt | date: 'short' }}</td>
                  <td class="actions" (click)="$event.stopPropagation()">
                    <button
                      class="btn btn-sm btn-primary"
                      (click)="confirmRetry(dl)"
                      [disabled]="actionInProgress()"
                    >
                      Retry
                    </button>
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

      @if (confirmingRetry()) {
        <div class="modal-overlay" (click)="closeModal()">
          <div class="modal" (click)="$event.stopPropagation()">
            <h3>Retry Dead Letter</h3>
            <p class="info-text">This will re-process the event through the processor.</p>
            <p class="warning-detail">
              Event: <strong>{{ confirmingRetry()?.eventType }}</strong><br />
              Processor: <strong>{{ confirmingRetry()?.processorId }}</strong>
            </p>
            @if (retryError()) {
              <p class="retry-error">{{ retryError() }}</p>
            }
            <div class="modal-actions">
              <button class="btn" (click)="closeModal()">Cancel</button>
              <button class="btn btn-primary" (click)="retryDeadLetter()" [disabled]="actionInProgress()">
                {{ actionInProgress() ? 'Retrying...' : 'Retry' }}
              </button>
            </div>
          </div>
        </div>
      }

      @if (showRetryAllModal()) {
        <div class="modal-overlay" (click)="closeModal()">
          <div class="modal" (click)="$event.stopPropagation()">
            <h3>Retry All Dead Letters</h3>
            <p class="info-text">
              This will retry all {{ totalCount() }} dead letters for processor:
            </p>
            <p class="warning-detail">
              <strong>{{ filterProcessorId() }}</strong>
            </p>
            @if (bulkRetryResult()) {
              <div class="bulk-result">
                <p class="success-count">Success: {{ bulkRetryResult()?.successCount }}</p>
                <p class="fail-count">Failed: {{ bulkRetryResult()?.failCount }}</p>
              </div>
            }
            @if (retryError()) {
              <p class="retry-error">{{ retryError() }}</p>
            }
            <div class="modal-actions">
              <button class="btn" (click)="closeModal()">{{ bulkRetryResult() ? 'Close' : 'Cancel' }}</button>
              @if (!bulkRetryResult()) {
                <button class="btn btn-primary" (click)="retryAllDeadLetters()" [disabled]="actionInProgress()">
                  {{ actionInProgress() ? 'Retrying...' : 'Retry All' }}
                </button>
              }
            </div>
          </div>
        </div>
      }
    </div>
  `,
  styles: `
    .dead-letters {
      width: 100%;
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
      flex-wrap: wrap;
    }

    .filter-bar {
      display: flex;
      flex-wrap: wrap;
      gap: 1rem;
      margin-bottom: 1.5rem;
      padding: 1rem;
      background: #1a1a1a;
      border: 1px solid #2a2a2a;
      border-radius: 8px;
      align-items: flex-end;
    }

    .filter-group {
      display: flex;
      flex-direction: column;
      gap: 0.375rem;

      label {
        font-size: 0.75rem;
        text-transform: uppercase;
        letter-spacing: 0.05em;
        color: #666;
      }

      select,
      input {
        padding: 0.5rem 0.75rem;
        background: #252525;
        border: 1px solid #3a3a3a;
        border-radius: 6px;
        font-size: 0.875rem;
        color: #e0e0e0;
        min-width: 150px;

        &:hover {
          border-color: #4a4a4a;
        }

        &:focus {
          outline: none;
          border-color: #6366f1;
        }
      }

      select option {
        background: #1a1a1a;
        color: #e0e0e0;
      }

      input[type="datetime-local"] {
        min-width: 180px;
      }

      &.search-group {
        flex: 1;
        min-width: 200px;

        input {
          width: 100%;
        }
      }
    }

    .btn-clear {
      background: transparent;
      border-color: #666;
      color: #888;

      &:hover:not(:disabled) {
        background: #252525;
        color: #e0e0e0;
      }
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

      tbody tr {
        cursor: pointer;
        transition: background 0.15s;

        &:hover {
          background: #1f1f1f;
        }

        &.selected {
          background: #252525;
        }

        &.new-item {
          animation: highlight-new 2s ease-out;
        }
      }
    }

    @keyframes highlight-new {
      0% { background: rgba(239, 68, 68, 0.3); }
      100% { background: transparent; }
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

    .info-text {
      color: #888;
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

    .retry-error {
      background: rgba(239, 68, 68, 0.1);
      border: 1px solid rgba(239, 68, 68, 0.3);
      border-radius: 6px;
      padding: 0.75rem;
      color: #ef4444;
      font-size: 0.875rem;
      margin-bottom: 1rem;
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

    .modal-actions {
      display: flex;
      justify-content: flex-end;
      gap: 0.75rem;
    }
  `,
})
export class DeadLettersComponent implements OnInit, OnDestroy {
  private readonly api = inject(AdminApiService);
  readonly subscriptionService = inject(AdminSubscriptionService);
  private subscription: Subscription | null = null;
  private searchSubject = new Subject<string>();

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly pagedResult = signal<PagedResult<DeadLetter> | null>(null);
  readonly selectedDeadLetter = signal<DeadLetter | null>(null);
  readonly confirmingRemove = signal<DeadLetter | null>(null);
  readonly confirmingRetry = signal<DeadLetter | null>(null);
  readonly retryError = signal<string | null>(null);
  readonly actionInProgress = signal(false);
  readonly recentlyAdded = signal<Set<string>>(new Set());
  readonly processors = signal<string[]>([]);
  readonly eventTypes = signal<string[]>([]);
  readonly filterProcessorId = signal<string | null>(null);
  readonly filterEventType = signal<string | null>(null);
  readonly filterSearchTerm = signal<string>('');
  readonly filterFailedAfter = signal<string>('');
  readonly filterFailedBefore = signal<string>('');
  readonly showRetryAllModal = signal(false);
  readonly bulkRetryResult = signal<BulkRetryResult | null>(null);

  readonly deadLetters = computed(() => this.pagedResult()?.items ?? []);
  readonly totalCount = computed(() => this.pagedResult()?.totalCount ?? 0);
  readonly page = computed(() => this.pagedResult()?.page ?? 1);
  readonly totalPages = computed(() => this.pagedResult()?.totalPages ?? 1);
  readonly selectedId = computed(() => this.selectedDeadLetter()?.id ?? null);
  readonly hasActiveFilters = computed(() =>
    !!this.filterProcessorId() ||
    !!this.filterEventType() ||
    !!this.filterSearchTerm() ||
    !!this.filterFailedAfter() ||
    !!this.filterFailedBefore()
  );

  ngOnInit(): void {
    this.loadProcessors();
    this.loadEventTypes();
    this.loadData();
    this.startSubscription();
    this.setupSearchDebounce();
  }

  private setupSearchDebounce(): void {
    this.searchSubject.pipe(
      debounceTime(300),
      distinctUntilChanged()
    ).subscribe(term => {
      this.filterSearchTerm.set(term);
      this.loadData(1);
    });
  }

  private loadProcessors(): void {
    this.api.getProcessors().subscribe({
      next: (procs) => {
        const ids = procs
          .filter(p => p.deadLetterCount > 0)
          .map(p => p.processorId);
        this.processors.set(ids);
      },
    });
  }

  private loadEventTypes(): void {
    this.api.getDeadLetterEventTypes().subscribe({
      next: (types) => {
        this.eventTypes.set(types);
      },
    });
  }

  onProcessorFilterChange(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const value = select.value || null;
    this.filterProcessorId.set(value);
    this.loadData(1);
  }

  onEventTypeFilterChange(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const value = select.value || null;
    this.filterEventType.set(value);
    this.loadData(1);
  }

  onSearchTermChange(term: string): void {
    this.searchSubject.next(term);
  }

  onFailedAfterChange(value: string): void {
    this.filterFailedAfter.set(value);
    this.loadData(1);
  }

  onFailedBeforeChange(value: string): void {
    this.filterFailedBefore.set(value);
    this.loadData(1);
  }

  clearFilters(): void {
    this.filterProcessorId.set(null);
    this.filterEventType.set(null);
    this.filterSearchTerm.set('');
    this.filterFailedAfter.set('');
    this.filterFailedBefore.set('');
    this.loadData(1);
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
    this.searchSubject.complete();
  }

  private startSubscription(): void {
    this.subscription = this.subscriptionService
      .subscribeToDeadLetters(this.api.moduleKey())
      .subscribe({
        next: (update) => {
          if (update.changeType === 'Added') {
            this.addDeadLetter(update.deadLetter);
            this.highlightDeadLetter(update.deadLetter.id);
          } else if (update.changeType === 'Removed') {
            this.removeDeadLetterFromList(update.deadLetter.id);
          }
        },
      });
  }

  private addDeadLetter(dl: DeadLetter): void {
    const current = this.pagedResult();
    if (!current) return;

    // Add to beginning of list on first page
    if (current.page === 1) {
      const newItems = [dl, ...current.items.slice(0, current.pageSize - 1)];
      this.pagedResult.set({
        ...current,
        items: newItems,
        totalCount: current.totalCount + 1,
        totalPages: Math.ceil((current.totalCount + 1) / current.pageSize),
      });
    } else {
      // Just update count if not on first page
      this.pagedResult.set({
        ...current,
        totalCount: current.totalCount + 1,
        totalPages: Math.ceil((current.totalCount + 1) / current.pageSize),
      });
    }
  }

  private removeDeadLetterFromList(id: string): void {
    const current = this.pagedResult();
    if (!current) return;

    const newItems = current.items.filter(dl => dl.id !== id);
    if (newItems.length !== current.items.length) {
      this.pagedResult.set({
        ...current,
        items: newItems,
        totalCount: Math.max(0, current.totalCount - 1),
        totalPages: Math.max(1, Math.ceil((current.totalCount - 1) / current.pageSize)),
      });

      // Clear selection if removed item was selected
      if (this.selectedDeadLetter()?.id === id) {
        this.selectedDeadLetter.set(null);
      }
    }
  }

  private highlightDeadLetter(id: string): void {
    const added = new Set(this.recentlyAdded());
    added.add(id);
    this.recentlyAdded.set(added);

    setTimeout(() => {
      const current = new Set(this.recentlyAdded());
      current.delete(id);
      this.recentlyAdded.set(current);
    }, 2000);
  }

  loadData(page: number = 1): void {
    this.loading.set(true);
    this.error.set(null);

    const filter: DeadLetterFilter = {};
    if (this.filterProcessorId()) filter.processorId = this.filterProcessorId()!;
    if (this.filterEventType()) filter.eventType = this.filterEventType()!;
    if (this.filterSearchTerm()) filter.searchTerm = this.filterSearchTerm();
    if (this.filterFailedAfter()) filter.failedAfter = new Date(this.filterFailedAfter()).toISOString();
    if (this.filterFailedBefore()) filter.failedBefore = new Date(this.filterFailedBefore()).toISOString();

    this.api.getDeadLetters(filter, page, 20).subscribe({
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

  confirmRetry(dl: DeadLetter): void {
    this.retryError.set(null);
    this.confirmingRetry.set(dl);
  }

  closeModal(): void {
    this.confirmingRemove.set(null);
    this.confirmingRetry.set(null);
    this.retryError.set(null);
    this.showRetryAllModal.set(false);
    this.bulkRetryResult.set(null);
  }

  confirmRetryAll(): void {
    this.retryError.set(null);
    this.bulkRetryResult.set(null);
    this.showRetryAllModal.set(true);
  }

  retryAllDeadLetters(): void {
    const processorId = this.filterProcessorId();
    if (!processorId) return;

    this.actionInProgress.set(true);
    this.retryError.set(null);

    this.api.retryAllDeadLetters(processorId).subscribe({
      next: (result) => {
        this.bulkRetryResult.set(result);
        this.loadData(1);
        this.loadProcessors();
        this.actionInProgress.set(false);
      },
      error: (err) => {
        this.retryError.set(err.message || 'Bulk retry failed');
        this.actionInProgress.set(false);
      },
    });
  }

  retryDeadLetter(): void {
    const dl = this.confirmingRetry();
    if (!dl) return;

    this.actionInProgress.set(true);
    this.retryError.set(null);

    this.api.retryDeadLetter(dl.id).subscribe({
      next: (result) => {
        if (result.success) {
          this.closeModal();
          if (this.selectedDeadLetter()?.id === dl.id) {
            this.selectedDeadLetter.set(null);
          }
          this.loadData(this.page());
        } else {
          this.retryError.set(result.errorMessage || 'Retry failed');
        }
        this.actionInProgress.set(false);
      },
      error: (err) => {
        this.retryError.set(err.error?.errorMessage || err.message || 'Retry failed');
        this.actionInProgress.set(false);
      },
    });
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
