import { Component, inject, OnInit, signal, DestroyRef, effect, computed } from '@angular/core';
import { DatePipe, SlicePipe, DecimalPipe } from '@angular/common';
import { AdminApiService } from '../../core/services/admin-api.service';
import { TenantLeaseInfo } from '../../core/models/admin.models';

@Component({
  selector: 'app-tenants',
  imports: [DatePipe, SlicePipe, DecimalPipe],
  template: `
    <div class="tenants">
      <header class="page-header">
        <div>
          <h1>Tenant Leases</h1>
          <p class="subtitle">Manage tenant ownership for blue-green deployments</p>
        </div>
        <div class="header-actions">
          @if (thisReplicaId()) {
            <div class="current-replica-badge">
              <span class="replica-label">This Replica:</span>
              <span class="replica-value mono">{{ thisReplicaId() | slice:0:8 }}</span>
            </div>
          }
          <button class="btn btn-secondary" (click)="loadData()">Refresh</button>
        </div>
      </header>

      @if (loading()) {
        <div class="loading">Loading tenant leases...</div>
      } @else if (error()) {
        <div class="error">
          <p>{{ error() }}</p>
          <button class="btn" (click)="loadData()">Retry</button>
        </div>
      } @else {
        @if (message()) {
          <div class="info-banner">{{ message() }}</div>
        }

        <!-- Distribution Overview -->
        <div class="distribution-panel">
          <div class="distribution-header">
            <h3>Tenant Distribution</h3>
            <span class="total-count">{{ leases().length }} total tenants across {{ replicaSummary().length }} replica(s)</span>
          </div>

          @if (leases().length > 0) {
            <div class="distribution-bar">
              @for (replica of replicaSummary(); track replica.replicaId) {
                <div
                  class="distribution-segment"
                  [class.is-current]="replica.replicaId === thisReplicaId()"
                  [style.width.%]="(replica.count / leases().length) * 100"
                  [title]="replica.replicaId + ': ' + replica.count + ' tenants'"
                >
                  @if ((replica.count / leases().length) > 0.1) {
                    <span class="segment-label">{{ replica.count }}</span>
                  }
                </div>
              }
            </div>

            <div class="distribution-legend">
              @for (replica of replicaSummary(); track replica.replicaId; let i = $index) {
                <div class="legend-item" [class.is-current]="replica.replicaId === thisReplicaId()">
                  <span class="legend-color" [style.background]="getReplicaColor(i, replica.replicaId === thisReplicaId())"></span>
                  <span class="legend-id mono">{{ replica.replicaId | slice:0:8 }}</span>
                  @if (replica.replicaId === thisReplicaId()) {
                    <span class="legend-badge">YOU</span>
                  }
                  <span class="legend-count">{{ replica.count }} ({{ (replica.count / leases().length * 100) | number:'1.0-0' }}%)</span>
                </div>
              }
            </div>
          } @else {
            <div class="no-tenants">No tenants found</div>
          }
        </div>

        <div class="action-panel">
          <h3>Deployment Actions</h3>
          <p class="action-description">
            Use these actions during blue-green deployments to control tenant ownership handoff.
          </p>
          <div class="action-buttons">
            <div class="action-group">
              <button
                class="btn btn-warning"
                (click)="drain()"
                [disabled]="actionInProgress()"
                title="Stop renewing leases and wait for other replicas to claim them"
              >
                Drain
              </button>
              <span class="action-hint">Stop renewing, wait for handoff (~60s)</span>
            </div>
            <div class="action-group">
              <button
                class="btn btn-primary"
                (click)="reclaim()"
                [disabled]="actionInProgress()"
                title="Clear cooldowns and immediately claim available tenants"
              >
                Reclaim
              </button>
              <span class="action-hint">Clear cooldowns, claim immediately</span>
            </div>
            <div class="action-group">
              <button
                class="btn btn-danger"
                (click)="release()"
                [disabled]="actionInProgress()"
                title="Release all leases immediately"
              >
                Release All
              </button>
              <span class="action-hint">Release all leases instantly</span>
            </div>
            <div class="action-group">
              <button
                class="btn btn-secondary"
                (click)="rebalance()"
                [disabled]="actionInProgress()"
                title="Release excess tenants if this replica has more than fair share"
              >
                Rebalance
              </button>
              <span class="action-hint">Release excess for fair distribution</span>
            </div>
          </div>
        </div>

        @if (leases().length > 0) {
          <div class="table-container">
            <table class="table">
              <thead>
                <tr>
                  <th>Tenant ID</th>
                  <th>Replica ID</th>
                  <th>Acquired At</th>
                  <th>Expires At</th>
                  <th>Status</th>
                </tr>
              </thead>
              <tbody>
                @for (lease of leases(); track lease.tenantId) {
                  <tr
                    [class.expiring]="isExpiringSoon(lease)"
                    [class.expired]="isExpired(lease)"
                    [class.owned-by-us]="lease.replicaId === thisReplicaId()"
                  >
                    <td class="tenant-id">{{ lease.tenantId }}</td>
                    <td class="replica-cell">
                      <span class="replica-id mono">{{ lease.replicaId | slice:0:8 }}</span>
                      @if (lease.replicaId === thisReplicaId()) {
                        <span class="you-badge">YOU</span>
                      }
                    </td>
                    <td class="muted">{{ lease.acquiredAt | date: 'short' }}</td>
                    <td class="muted">{{ lease.expiresAt | date: 'short' }}</td>
                    <td>
                      @if (isExpired(lease)) {
                        <span class="status-badge expired">Expired</span>
                      } @else if (isExpiringSoon(lease)) {
                        <span class="status-badge expiring">Expiring</span>
                      } @else {
                        <span class="status-badge active">Active</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      }
    </div>
  `,
  styles: `
    .tenants {
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

    .current-replica-badge {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.5rem 0.75rem;
      background: rgba(139, 92, 246, 0.15);
      border: 1px solid rgba(139, 92, 246, 0.3);
      border-radius: 6px;
    }

    .replica-label {
      font-size: 0.75rem;
      color: #888;
    }

    .replica-value {
      font-size: 0.875rem;
      color: #a78bfa;
      font-weight: 500;
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

    .info-banner {
      padding: 0.75rem 1rem;
      background: #1e3a5f;
      border: 1px solid #2563eb;
      border-radius: 6px;
      color: #93c5fd;
      margin-bottom: 1.5rem;
    }

    /* Distribution Panel */
    .distribution-panel {
      background: #1a1a1a;
      border: 1px solid #2a2a2a;
      border-radius: 8px;
      padding: 1.5rem;
      margin-bottom: 1.5rem;
    }

    .distribution-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 1rem;

      h3 {
        font-size: 1rem;
        font-weight: 600;
        color: #fff;
        margin: 0;
      }
    }

    .total-count {
      font-size: 0.875rem;
      color: #888;
    }

    .distribution-bar {
      display: flex;
      height: 32px;
      border-radius: 6px;
      overflow: hidden;
      margin-bottom: 1rem;
      background: #252525;
    }

    .distribution-segment {
      display: flex;
      align-items: center;
      justify-content: center;
      min-width: 4px;
      transition: all 0.2s;

      &:nth-child(1) { background: #3b82f6; }
      &:nth-child(2) { background: #22c55e; }
      &:nth-child(3) { background: #f59e0b; }
      &:nth-child(4) { background: #ec4899; }
      &:nth-child(5) { background: #8b5cf6; }
      &:nth-child(6) { background: #14b8a6; }
      &:nth-child(n+7) { background: #6b7280; }

      &.is-current {
        background: #8b5cf6 !important;
        box-shadow: inset 0 0 0 2px rgba(255, 255, 255, 0.3);
      }
    }

    .segment-label {
      font-size: 0.75rem;
      font-weight: 600;
      color: #fff;
      text-shadow: 0 1px 2px rgba(0, 0, 0, 0.3);
    }

    .distribution-legend {
      display: flex;
      flex-wrap: wrap;
      gap: 1rem;
    }

    .legend-item {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.375rem 0.75rem;
      background: #252525;
      border-radius: 4px;

      &.is-current {
        background: rgba(139, 92, 246, 0.15);
        border: 1px solid rgba(139, 92, 246, 0.3);
      }
    }

    .legend-color {
      width: 12px;
      height: 12px;
      border-radius: 3px;
    }

    .legend-id {
      font-size: 0.8125rem;
      color: #a0a0a0;
    }

    .legend-badge, .you-badge {
      font-size: 0.625rem;
      font-weight: 600;
      padding: 0.125rem 0.375rem;
      background: #8b5cf6;
      color: #fff;
      border-radius: 3px;
      letter-spacing: 0.05em;
    }

    .legend-count {
      font-size: 0.8125rem;
      color: #fff;
      font-weight: 500;
    }

    .no-tenants {
      color: #666;
      text-align: center;
      padding: 1rem;
    }

    .action-panel {
      background: #1a1a1a;
      border: 1px solid #2a2a2a;
      border-radius: 8px;
      padding: 1.5rem;
      margin-bottom: 1.5rem;

      h3 {
        font-size: 1rem;
        font-weight: 600;
        color: #fff;
        margin-bottom: 0.5rem;
      }
    }

    .action-description {
      color: #888;
      font-size: 0.875rem;
      margin-bottom: 1rem;
    }

    .action-buttons {
      display: flex;
      flex-wrap: wrap;
      gap: 1.5rem;
    }

    .action-group {
      display: flex;
      flex-direction: column;
      gap: 0.375rem;
    }

    .action-hint {
      font-size: 0.75rem;
      color: #666;
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

      &.btn-warning {
        background: #d97706;
        border-color: #d97706;
        color: #fff;

        &:hover:not(:disabled) {
          background: #b45309;
        }
      }

      &.btn-danger {
        background: #dc2626;
        border-color: #dc2626;
        color: #fff;

        &:hover:not(:disabled) {
          background: #b91c1c;
        }
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

      tr.expiring {
        background: rgba(245, 158, 11, 0.05);
      }

      tr.expired {
        background: rgba(239, 68, 68, 0.05);
        opacity: 0.7;
      }

      tr.owned-by-us {
        background: rgba(139, 92, 246, 0.08);
      }
    }

    .tenant-id {
      font-weight: 500;
      color: #fff;
    }

    .replica-cell {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }

    .mono {
      font-family: 'SF Mono', Monaco, monospace;
      font-size: 0.875rem;
    }

    .replica-id {
      color: #a0a0a0;
    }

    .muted {
      color: #666;
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

      &.expiring {
        background: rgba(245, 158, 11, 0.15);
        color: #f59e0b;
      }

      &.expired {
        background: rgba(239, 68, 68, 0.15);
        color: #ef4444;
      }
    }
  `,
})
export class TenantsComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly leases = signal<TenantLeaseInfo[]>([]);
  readonly thisReplicaId = signal<string | null>(null);
  readonly ownedByThisReplica = signal(0);
  readonly message = signal<string | null>(null);
  readonly actionInProgress = signal(false);

  private currentModuleKey: string | null = null;

  private readonly colors = [
    '#3b82f6', '#22c55e', '#f59e0b', '#ec4899', '#8b5cf6', '#14b8a6', '#6b7280'
  ];

  constructor() {
    effect(() => {
      const moduleKey = this.api.moduleKey();
      if (this.currentModuleKey !== null && this.currentModuleKey !== moduleKey) {
        this.loadData();
      }
      this.currentModuleKey = moduleKey;
    });
  }

  ngOnInit(): void {
    this.loadData();
  }

  replicaSummary(): { replicaId: string; count: number }[] {
    const counts = new Map<string, number>();
    for (const lease of this.leases()) {
      counts.set(lease.replicaId, (counts.get(lease.replicaId) || 0) + 1);
    }
    // Sort so "this replica" appears first, then by count descending
    return Array.from(counts.entries())
      .map(([replicaId, count]) => ({ replicaId, count }))
      .sort((a, b) => {
        if (a.replicaId === this.thisReplicaId()) return -1;
        if (b.replicaId === this.thisReplicaId()) return 1;
        return b.count - a.count;
      });
  }

  getReplicaColor(index: number, isThisReplica: boolean): string {
    if (isThisReplica) return '#8b5cf6';
    return this.colors[index % this.colors.length];
  }

  isExpired(lease: TenantLeaseInfo): boolean {
    return new Date(lease.expiresAt) < new Date();
  }

  isExpiringSoon(lease: TenantLeaseInfo): boolean {
    const expiresAt = new Date(lease.expiresAt);
    const now = new Date();
    const thirtySecondsFromNow = new Date(now.getTime() + 30000);
    return expiresAt > now && expiresAt < thirtySecondsFromNow;
  }

  loadData(): void {
    this.loading.set(true);
    this.error.set(null);
    this.message.set(null);

    this.api.getTenantLeases().subscribe({
      next: (response) => {
        this.leases.set(response.leases);
        this.thisReplicaId.set(response.thisReplicaId);
        this.ownedByThisReplica.set(response.ownedByThisReplica);
        if (response.message) {
          this.message.set(response.message);
        }
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err.message || 'Failed to load tenant leases');
        this.loading.set(false);
      },
    });
  }

  drain(): void {
    if (
      !confirm(
        'Are you sure you want to drain this replica? It will stop renewing leases and wait for other replicas to claim them. This may take up to 60 seconds.'
      )
    ) {
      return;
    }

    this.actionInProgress.set(true);
    this.api.drainTenants().subscribe({
      next: () => {
        this.actionInProgress.set(false);
        this.loadData();
      },
      error: (err) => {
        this.actionInProgress.set(false);
        this.error.set(err.message || 'Failed to drain tenants');
      },
    });
  }

  reclaim(): void {
    this.actionInProgress.set(true);
    this.api.reclaimTenants().subscribe({
      next: () => {
        this.actionInProgress.set(false);
        this.loadData();
      },
      error: (err) => {
        this.actionInProgress.set(false);
        this.error.set(err.message || 'Failed to reclaim tenants');
      },
    });
  }

  release(): void {
    if (
      !confirm(
        'Are you sure you want to release all tenant leases immediately? Other replicas will need to reclaim them.'
      )
    ) {
      return;
    }

    this.actionInProgress.set(true);
    this.api.releaseTenants().subscribe({
      next: () => {
        this.actionInProgress.set(false);
        this.loadData();
      },
      error: (err) => {
        this.actionInProgress.set(false);
        this.error.set(err.message || 'Failed to release tenants');
      },
    });
  }

  rebalance(): void {
    this.actionInProgress.set(true);
    this.api.rebalanceTenants().subscribe({
      next: (result) => {
        this.actionInProgress.set(false);
        if (result.releasedCount > 0) {
          this.message.set(`Released ${result.releasedCount} tenant(s) for rebalancing`);
        } else {
          this.message.set('No rebalancing needed - distribution is already fair');
        }
        this.loadData();
      },
      error: (err) => {
        this.actionInProgress.set(false);
        this.error.set(err.message || 'Failed to rebalance tenants');
      },
    });
  }
}
