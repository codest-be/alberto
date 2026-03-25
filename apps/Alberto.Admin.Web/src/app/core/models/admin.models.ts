export interface ProcessorStatus {
  processorId: string;
  isActive: boolean;
  isRebuilding: boolean;
  lastPosition: number | null;
  globalPosition: number;
  lag: number;
  lastUpdated: string | null;
  handledEventTypes: string[];
  deadLetterCount: number;
}

export interface Checkpoint {
  processorId: string;
  lastPosition: number;
  updatedAt: string;
}

export interface DeadLetter {
  id: string;
  processorId: string;
  eventId: string;
  eventType: string;
  eventData: string;
  errorMessage: string;
  stackTrace: string | null;
  attemptCount: number;
  failedAt: string;
}

export interface ProjectionState {
  tenantId: string;
  projectionType: string;
  documentId: string;
  state: string;
  updatedAt: string;
}

export interface SystemInfo {
  moduleKey: string;
  globalPosition: number;
  processorCount: number;
  deadLetterCount: number;
  readOnlyMode: boolean;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export interface ModuleInfo {
  moduleKey: string;
  title: string;
  readOnly: boolean;
}

export interface DeadLetterRetryResult {
  id: string;
  success: boolean;
  errorMessage: string | null;
}

export interface BulkRetryResult {
  totalAttempted: number;
  successCount: number;
  failCount: number;
  results: DeadLetterRetryResult[];
}

export interface BulkOperationResult {
  totalCount: number;
  successCount: number;
  failCount: number;
  items: OperationItemResult[];
}

export interface OperationItemResult {
  id: string;
  success: boolean;
  errorMessage: string | null;
}

export type RebuildState =
  | 'NotStarted'
  | 'Clearing'
  | 'Rebuilding'
  | 'Completed'
  | 'Failed'
  | 'Cancelled';

export interface RebuildStatus {
  processorId: string;
  state: RebuildState;
  currentPosition: number;
  targetPosition: number;
  progressPercent: number;
  startedAt: string | null;
  completedAt: string | null;
  errorMessage: string | null;
}

export interface DeadLetterFilter {
  processorId?: string;
  eventType?: string;
  searchTerm?: string;
  failedAfter?: string;
  failedBefore?: string;
}

export interface ProjectionFilter {
  tenantId?: string;
  searchTerm?: string;
  updatedAfter?: string;
  updatedBefore?: string;
}

export interface TenantLeaseInfo {
  tenantId: string;
  replicaId: string;
  acquiredAt: string;
  expiresAt: string;
}

export interface TenantLeasesResponse {
  leases: TenantLeaseInfo[];
  thisReplicaId: string | null;
  ownedByThisReplica: number;
  message?: string;
}

export interface TenantOperationResult {
  status: string;
  ownedTenants: number;
}

export interface RebalanceResult {
  status: string;
  releasedCount: number;
  ownedTenants: number;
}
