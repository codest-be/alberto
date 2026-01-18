import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  ProcessorStatus,
  Checkpoint,
  DeadLetter,
  DeadLetterFilter,
  ProjectionState,
  ProjectionFilter,
  SystemInfo,
  PagedResult,
  ModuleInfo,
  DeadLetterRetryResult,
  BulkRetryResult,
  BulkOperationResult,
  RebuildStatus,
} from '../models/admin.models';

const STORAGE_KEY = 'selectedModule';
const DEFAULT_MODULE = 'orders';

function getInitialModuleKey(): string {
  if (typeof localStorage !== 'undefined') {
    return localStorage.getItem(STORAGE_KEY) || DEFAULT_MODULE;
  }
  return DEFAULT_MODULE;
}

@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly http = inject(HttpClient);

  readonly baseUrl = signal('/alberto');
  readonly moduleKey = signal(getInitialModuleKey());

  setModuleKey(key: string): void {
    this.moduleKey.set(key);
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem(STORAGE_KEY, key);
    }
  }

  private get apiBase(): string {
    return `${this.baseUrl()}/${this.moduleKey()}/api`;
  }

  // Modules
  getModules(): Observable<ModuleInfo[]> {
    return this.http.get<ModuleInfo[]>(`${this.baseUrl()}/api/modules`);
  }

  // System
  getSystemInfo(): Observable<SystemInfo> {
    return this.http.get<SystemInfo>(`${this.apiBase}/system/info`);
  }

  getGlobalPosition(): Observable<{ position: number }> {
    return this.http.get<{ position: number }>(`${this.apiBase}/system/position`);
  }

  // Processors
  getProcessors(): Observable<ProcessorStatus[]> {
    return this.http.get<ProcessorStatus[]>(`${this.apiBase}/processors`);
  }

  activateProcessor(processorId: string): Observable<void> {
    return this.http.post<void>(`${this.apiBase}/processors/${processorId}/activate`, {});
  }

  deactivateProcessor(processorId: string): Observable<void> {
    return this.http.post<void>(`${this.apiBase}/processors/${processorId}/deactivate`, {});
  }

  // Checkpoints
  getCheckpoints(): Observable<Checkpoint[]> {
    return this.http.get<Checkpoint[]>(`${this.apiBase}/checkpoints`);
  }

  resetCheckpoint(processorId: string): Observable<void> {
    return this.http.delete<void>(`${this.apiBase}/checkpoints/${processorId}`);
  }

  setCheckpoint(processorId: string, position: number): Observable<void> {
    return this.http.put<void>(`${this.apiBase}/checkpoints/${processorId}`, { position });
  }

  resetMultipleCheckpoints(processorIds: string[]): Observable<BulkOperationResult> {
    return this.http.post<BulkOperationResult>(`${this.apiBase}/checkpoints/reset-multiple`, { processorIds });
  }

  // Dead Letters
  getDeadLetters(
    filter?: DeadLetterFilter,
    page: number = 1,
    pageSize: number = 50
  ): Observable<PagedResult<DeadLetter>> {
    const params: Record<string, string | number> = { page, pageSize };
    if (filter?.processorId) {
      params['processorId'] = filter.processorId;
    }
    if (filter?.eventType) {
      params['eventType'] = filter.eventType;
    }
    if (filter?.searchTerm) {
      params['searchTerm'] = filter.searchTerm;
    }
    if (filter?.failedAfter) {
      params['failedAfter'] = filter.failedAfter;
    }
    if (filter?.failedBefore) {
      params['failedBefore'] = filter.failedBefore;
    }
    return this.http.get<PagedResult<DeadLetter>>(`${this.apiBase}/dead-letters`, { params });
  }

  getDeadLetterEventTypes(): Observable<string[]> {
    return this.http.get<string[]>(`${this.apiBase}/dead-letters/event-types`);
  }

  getDeadLetterCount(processorId?: string): Observable<{ count: number }> {
    const params: Record<string, string> = {};
    if (processorId) {
      params['processorId'] = processorId;
    }
    return this.http.get<{ count: number }>(`${this.apiBase}/dead-letters/count`, { params });
  }

  getDeadLetter(id: string): Observable<DeadLetter> {
    return this.http.get<DeadLetter>(`${this.apiBase}/dead-letters/${id}`);
  }

  removeDeadLetter(id: string): Observable<void> {
    return this.http.delete<void>(`${this.apiBase}/dead-letters/${id}`);
  }

  retryDeadLetter(id: string): Observable<DeadLetterRetryResult> {
    return this.http.post<DeadLetterRetryResult>(`${this.apiBase}/dead-letters/${id}/retry`, {});
  }

  clearDeadLetters(processorId: string): Observable<void> {
    return this.http.delete<void>(`${this.apiBase}/dead-letters`, {
      params: { processorId },
    });
  }

  retryAllDeadLetters(processorId: string): Observable<BulkRetryResult> {
    return this.http.post<BulkRetryResult>(`${this.apiBase}/dead-letters/retry-all`, null, {
      params: { processorId },
    });
  }

  // Projection States
  getProjectionTypes(): Observable<string[]> {
    return this.http.get<string[]>(`${this.apiBase}/projection-states/types`);
  }

  getProjectionTenants(projectionType: string): Observable<string[]> {
    return this.http.get<string[]>(`${this.apiBase}/projection-states/${projectionType}/tenants`);
  }

  getProjectionStates(
    projectionType: string,
    filter?: ProjectionFilter,
    page: number = 1,
    pageSize: number = 50
  ): Observable<PagedResult<ProjectionState>> {
    const params: Record<string, string | number> = { page, pageSize };
    if (filter?.tenantId) {
      params['tenantId'] = filter.tenantId;
    }
    if (filter?.searchTerm) {
      params['searchTerm'] = filter.searchTerm;
    }
    if (filter?.updatedAfter) {
      params['updatedAfter'] = filter.updatedAfter;
    }
    if (filter?.updatedBefore) {
      params['updatedBefore'] = filter.updatedBefore;
    }
    return this.http.get<PagedResult<ProjectionState>>(
      `${this.apiBase}/projection-states/${projectionType}`,
      { params }
    );
  }

  getProjectionState(
    projectionType: string,
    documentId: string,
    tenantId?: string
  ): Observable<ProjectionState> {
    const params: Record<string, string> = {};
    if (tenantId) {
      params['tenantId'] = tenantId;
    }
    return this.http.get<ProjectionState>(
      `${this.apiBase}/projection-states/${projectionType}/${documentId}`,
      { params }
    );
  }

  // Processor Rebuilds (Legacy - clears state)
  startRebuild(processorId: string, clearState: boolean = true): Observable<RebuildStatus> {
    return this.http.post<RebuildStatus>(
      `${this.apiBase}/processors/${encodeURIComponent(processorId)}/rebuild`,
      null,
      { params: { clearState: clearState.toString() } }
    );
  }

  getRebuildStatus(processorId: string): Observable<RebuildStatus | null> {
    return this.http.get<RebuildStatus | null>(
      `${this.apiBase}/processors/${encodeURIComponent(processorId)}/rebuild/status`
    );
  }

  cancelRebuild(processorId: string): Observable<void> {
    return this.http.post<void>(
      `${this.apiBase}/processors/${encodeURIComponent(processorId)}/rebuild/cancel`,
      {}
    );
  }

  // Versioned Rebuilds (Zero-downtime)
  startVersionedRebuild(processorId: string): Observable<RebuildStatus> {
    return this.http.post<RebuildStatus>(
      `${this.apiBase}/processors/${encodeURIComponent(processorId)}/rebuild/start`,
      {}
    );
  }

  swapRebuildVersion(processorId: string): Observable<RebuildStatus> {
    return this.http.post<RebuildStatus>(
      `${this.apiBase}/processors/${encodeURIComponent(processorId)}/rebuild/swap`,
      {}
    );
  }

  rollbackRebuild(processorId: string): Observable<RebuildStatus> {
    return this.http.post<RebuildStatus>(
      `${this.apiBase}/processors/${encodeURIComponent(processorId)}/rebuild/rollback`,
      {}
    );
  }

  cleanupOldVersion(processorId: string, version: number): Observable<void> {
    return this.http.post<void>(
      `${this.apiBase}/processors/${encodeURIComponent(processorId)}/rebuild/cleanup/${version}`,
      {}
    );
  }
}
