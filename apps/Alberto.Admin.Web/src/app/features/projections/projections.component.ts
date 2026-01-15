import { Component, inject, OnInit, signal, computed } from '@angular/core';
import { DatePipe } from '@angular/common';
import { AdminApiService } from '../../core/services/admin-api.service';
import { ProjectionState, PagedResult } from '../../core/models/admin.models';

@Component({
  selector: 'app-projections',
  imports: [DatePipe],
  template: `
    <div class="projections">
      <header class="page-header">
        <div>
          <h1>Projections</h1>
          <p class="subtitle">View projection state documents</p>
        </div>
        <button class="btn btn-secondary" (click)="loadTypes()">Refresh</button>
      </header>

      @if (loadingTypes()) {
        <div class="loading">Loading projection types...</div>
      } @else if (error()) {
        <div class="error">
          <p>{{ error() }}</p>
          <button class="btn" (click)="loadTypes()">Retry</button>
        </div>
      } @else {
        <div class="type-selector">
          <label>Projection Type</label>
          <div class="type-buttons">
            @for (type of projectionTypes(); track type) {
              <button
                class="type-btn"
                [class.active]="selectedType() === type"
                (click)="selectType(type)"
              >
                {{ type }}
              </button>
            }
            @if (projectionTypes().length === 0) {
              <span class="no-types">No projection types found</span>
            }
          </div>
        </div>

        @if (selectedType()) {
          @if (loadingStates()) {
            <div class="loading">Loading projection states...</div>
          } @else if (projectionStates().length === 0) {
            <div class="empty">No projection states found for {{ selectedType() }}</div>
          } @else {
            <div class="content-layout" [class.has-detail]="selectedState()">
              <div class="list-section">
                <div class="table-container">
                  <table class="table">
                    <thead>
                      <tr>
                        <th>Document ID</th>
                        <th>Tenant</th>
                        <th>Updated At</th>
                        <th>Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (state of projectionStates(); track state.tenantId + ':' + state.documentId) {
                        <tr
                          (click)="selectState(state)"
                          [class.selected]="selectedState()?.documentId === state.documentId && selectedState()?.tenantId === state.tenantId"
                        >
                          <td class="doc-id">{{ state.documentId }}</td>
                          <td class="tenant">{{ state.tenantId || '-' }}</td>
                          <td class="muted">{{ state.updatedAt | date: 'medium' }}</td>
                          <td class="actions" (click)="$event.stopPropagation()">
                            <button class="btn btn-sm" (click)="selectState(state)">View</button>
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
              </div>

              @if (selectedState()) {
                <div class="detail-panel">
                  <div class="detail-header">
                    <h3>Projection State</h3>
                    <button class="btn btn-sm" (click)="selectedState.set(null)">Close</button>
                  </div>

                  <div class="detail-grid">
                    <div class="detail-item">
                      <span class="label">Projection Type</span>
                      <span class="value">{{ selectedState()?.projectionType }}</span>
                    </div>
                    <div class="detail-item">
                      <span class="label">Document ID</span>
                      <span class="value mono">{{ selectedState()?.documentId }}</span>
                    </div>
                    <div class="detail-item">
                      <span class="label">Tenant ID</span>
                      <span class="value">{{ selectedState()?.tenantId || '-' }}</span>
                    </div>
                    <div class="detail-item">
                      <span class="label">Updated At</span>
                      <span class="value">{{ selectedState()?.updatedAt | date: 'medium' }}</span>
                    </div>
                  </div>

                  <div class="detail-section">
                    <h4>State</h4>
                    <pre class="json-block">{{ formatJson(selectedState()?.state) }}</pre>
                  </div>
                </div>
              }
            </div>
          }
        }
      }
    </div>
  `,
  styles: `
    .projections {
      width: 100%;
    }

    .content-layout {
      display: flex;
      gap: 1.5rem;

      &.has-detail {
        .list-section {
          flex: 1;
          min-width: 0;
        }
      }
    }

    .list-section {
      flex: 1;
      min-width: 0;
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

      &.btn-secondary {
        background: transparent;
      }
    }

    .type-selector {
      margin-bottom: 1.5rem;

      label {
        display: block;
        font-size: 0.75rem;
        text-transform: uppercase;
        letter-spacing: 0.05em;
        color: #666;
        margin-bottom: 0.75rem;
      }
    }

    .type-buttons {
      display: flex;
      flex-wrap: wrap;
      gap: 0.5rem;
    }

    .type-btn {
      padding: 0.5rem 1rem;
      background: #1a1a1a;
      border: 1px solid #2a2a2a;
      border-radius: 6px;
      color: #888;
      cursor: pointer;
      font-size: 0.875rem;
      transition: all 0.15s;

      &:hover {
        background: #252525;
        color: #e0e0e0;
      }

      &.active {
        background: #6366f1;
        border-color: #6366f1;
        color: #fff;
      }
    }

    .no-types {
      color: #666;
      font-size: 0.875rem;
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

    .doc-id {
      font-family: 'SF Mono', Monaco, monospace;
      font-size: 0.8125rem;
      color: #8b5cf6;
    }

    .tenant {
      color: #e0e0e0;
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
      width: 500px;
      flex-shrink: 0;
      background: #1a1a1a;
      border: 1px solid #2a2a2a;
      border-radius: 8px;
      padding: 1.25rem;
      height: fit-content;
      position: sticky;
      top: 1rem;
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

    .json-block {
      background: #151515;
      border: 1px solid #252525;
      border-radius: 6px;
      padding: 1rem;
      font-family: 'SF Mono', Monaco, monospace;
      font-size: 0.8125rem;
      color: #e0e0e0;
      overflow-x: auto;
      white-space: pre-wrap;
      word-break: break-word;
      max-height: 400px;
      overflow-y: auto;
    }
  `,
})
export class ProjectionsComponent implements OnInit {
  private readonly api = inject(AdminApiService);

  readonly loadingTypes = signal(true);
  readonly loadingStates = signal(false);
  readonly error = signal<string | null>(null);
  readonly projectionTypes = signal<string[]>([]);
  readonly selectedType = signal<string | null>(null);
  readonly pagedResult = signal<PagedResult<ProjectionState> | null>(null);
  readonly selectedState = signal<ProjectionState | null>(null);

  readonly projectionStates = computed(() => this.pagedResult()?.items ?? []);
  readonly page = computed(() => this.pagedResult()?.page ?? 1);
  readonly totalPages = computed(() => this.pagedResult()?.totalPages ?? 1);

  ngOnInit(): void {
    this.loadTypes();
  }

  loadTypes(): void {
    this.loadingTypes.set(true);
    this.error.set(null);

    this.api.getProjectionTypes().subscribe({
      next: (types) => {
        this.projectionTypes.set(types);
        this.loadingTypes.set(false);

        if (types.length > 0 && !this.selectedType()) {
          this.selectType(types[0]);
        }
      },
      error: (err) => {
        this.error.set(err.message || 'Failed to load projection types');
        this.loadingTypes.set(false);
      },
    });
  }

  selectType(type: string): void {
    this.selectedType.set(type);
    this.selectedState.set(null);
    this.loadStates(1);
  }

  loadStates(page: number): void {
    const type = this.selectedType();
    if (!type) return;

    this.loadingStates.set(true);

    this.api.getProjectionStates(type, undefined, page, 20).subscribe({
      next: (result) => {
        this.pagedResult.set(result);
        this.loadingStates.set(false);
      },
      error: () => {
        this.loadingStates.set(false);
      },
    });
  }

  goToPage(page: number): void {
    this.loadStates(page);
  }

  selectState(state: ProjectionState): void {
    this.selectedState.set(state);
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
