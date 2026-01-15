import { Injectable, inject, signal } from '@angular/core';
import { Apollo, gql } from 'apollo-angular';
import { Observable, map, interval, switchMap, catchError, of, filter, tap, timer, retry, Subject, takeUntil } from 'rxjs';
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
    const wsConnected$ = new Subject<void>();

    // WebSocket subscription - primary method
    return this.apollo
      .subscribe<{ onProcessorStatusUpdated: ProcessorStatusUpdate }>({
        query: PROCESSOR_STATUS_SUBSCRIPTION,
        variables: { moduleKey, processorId },
      })
      .pipe(
        tap(() => {
          if (!this.wsConnected()) {
            console.log('[ProcessorSubscription] WebSocket connected');
            this.wsConnected.set(true);
            this.usingPolling.set(false);
            wsConnected$.next();
          }
        }),
        map((result) => {
          if (!result.data) {
            throw new Error('No data received from subscription');
          }
          return result.data.onProcessorStatusUpdated;
        }),
        catchError((err) => {
          console.warn('[ProcessorSubscription] WebSocket error:', err.message);
          this.wsConnected.set(false);
          this.usingPolling.set(true);

          // Fall back to polling
          return this.createPollingFallback(moduleKey, processorId).pipe(
            takeUntil(wsConnected$)
          );
        }),
        // Retry WebSocket connection with exponential backoff
        retry({
          delay: (error, retryCount) => {
            const delay = Math.min(1000 * Math.pow(2, retryCount), 30000);
            console.log(`[ProcessorSubscription] Retrying WebSocket in ${delay}ms (attempt ${retryCount + 1})`);
            return timer(delay);
          },
        })
      );
  }

  private createPollingFallback(
    moduleKey?: string,
    processorId?: string
  ): Observable<ProcessorStatusUpdate> {
    console.log('[ProcessorSubscription] Using polling fallback');

    return interval(2000).pipe(
      switchMap(() => this.api.getProcessors().pipe(
        catchError(() => of([]))
      )),
      switchMap((processors) => {
        return of(...processors.map(processor => ({
          moduleKey: moduleKey || this.api.moduleKey(),
          processor
        })));
      }),
      filter((update): update is ProcessorStatusUpdate => {
        if (processorId && update.processor.processorId !== processorId) {
          return false;
        }
        return true;
      })
    );
  }
}
