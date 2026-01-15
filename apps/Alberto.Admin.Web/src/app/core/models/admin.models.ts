export interface ProcessorStatus {
  processorId: string;
  isActive: boolean;
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
