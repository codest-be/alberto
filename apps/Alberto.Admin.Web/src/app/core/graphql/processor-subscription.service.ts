import { Injectable, inject, signal } from '@angular/core';
import { Apollo, gql } from 'apollo-angular';
import { Observable, map, interval, switchMap, catchError, of, merge, filter, distinctUntilChanged, tap } from 'rxjs';
import { ProcessorStatus } from '../models/admin.models';
import { AdminApiService } from '../services/admin-api.service';

const PROCESSOR_STATUS_SUBSCRIPTION = gql`
  subscription OnProcessorStatusUpdated($moduleKey: String, $processorId: String) {
    onProcessorStatusUpdated(moduleKey: $moduleKey, processorId: $processorId) {
      moduleKey
      processor {
        processorId
        isActive
        lastPosition
        globalPosition
        lag
        lastUpdated
        handledEventTypes
        deadLetterCount
      }
    }
  }
`;

export interface ProcessorStatusUpdate {
  moduleKey: string;
  processor: ProcessorStatus;
}

@Injectable({ providedIn: 'root' })
export class ProcessorSubscriptionService {
  private readonly apollo = inject(Apollo);
  private readonly api = inject(AdminApiService);

  /** Whether WebSocket connection is active */
  readonly wsConnected = signal(false);

  /** Whether using polling fallback */
  readonly usingPolling = signal(false);

  /**
   * Subscribes to real-time processor status updates.
   * Uses WebSocket subscription with polling fallback.
   * @param moduleKey Optional filter by module key
   * @param processorId Optional filter by processor ID
   */
  subscribeToProcessorStatus(
    moduleKey?: string,
    processorId?: string
  ): Observable<ProcessorStatusUpdate> {
    // Try WebSocket subscription first
    const wsSubscription = this.apollo
      .subscribe<{ onProcessorStatusUpdated: ProcessorStatusUpdate }>({
        query: PROCESSOR_STATUS_SUBSCRIPTION,
        variables: { moduleKey, processorId },
      })
      .pipe(
        tap(() => {
          this.wsConnected.set(true);
          this.usingPolling.set(false);
        }),
        map((result) => {
          if (!result.data) {
            throw new Error('No data received from subscription');
          }
          return result.data.onProcessorStatusUpdated;
        }),
        catchError((err) => {
          console.warn('[ProcessorSubscription] WebSocket failed, using polling fallback:', err.message);
          this.wsConnected.set(false);
          this.usingPolling.set(true);
          return of(null);
        }),
        filter((update): update is ProcessorStatusUpdate => update !== null)
      );

    // Polling fallback - polls every 2 seconds when WebSocket fails
    const pollingFallback = interval(2000).pipe(
      filter(() => !this.wsConnected()),
      tap(() => this.usingPolling.set(true)),
      switchMap(() => this.api.getProcessors().pipe(
        catchError(() => of([]))
      )),
      switchMap((processors) => {
        // Emit an update for each processor
        return of(...processors.map(processor => ({
          moduleKey: moduleKey || this.api.moduleKey(),
          processor
        })));
      }),
      filter((update): update is ProcessorStatusUpdate => {
        // Filter by processorId if specified
        if (processorId && update.processor.processorId !== processorId) {
          return false;
        }
        return true;
      })
    );

    return merge(wsSubscription, pollingFallback);
  }
}
