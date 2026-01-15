import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  ProcessorStatus,
  Checkpoint,
  DeadLetter,
  ProjectionState,
  SystemInfo,
  PagedResult,
} from '../models/admin.models';

@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly http = inject(HttpClient);

  readonly baseUrl = signal('/alberto');
  readonly moduleKey = signal('orders');

  private get apiBase(): string {
    return `${this.baseUrl()}/${this.moduleKey()}/api`;
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

  // Dead Letters
  getDeadLetters(
    processorId?: string,
    page: number = 1,
    pageSize: number = 50
  ): Observable<PagedResult<DeadLetter>> {
    const params: Record<string, string | number> = { page, pageSize };
    if (processorId) {
      params['processorId'] = processorId;
    }
    return this.http.get<PagedResult<DeadLetter>>(`${this.apiBase}/dead-letters`, { params });
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

  clearDeadLetters(processorId: string): Observable<void> {
    return this.http.delete<void>(`${this.apiBase}/dead-letters`, {
      params: { processorId },
    });
  }

  // Projection States
  getProjectionTypes(): Observable<string[]> {
    return this.http.get<string[]>(`${this.apiBase}/projection-states/types`);
  }

  getProjectionStates(
    projectionType: string,
    tenantId?: string,
    page: number = 1,
    pageSize: number = 50
  ): Observable<PagedResult<ProjectionState>> {
    const params: Record<string, string | number> = { page, pageSize };
    if (tenantId) {
      params['tenantId'] = tenantId;
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
}
