import { Injectable, inject } from '@angular/core';
import { Apollo, gql } from 'apollo-angular';
import { Observable, map, retry, timer } from 'rxjs';
import { ProcessorStatus, Checkpoint, DeadLetter, SystemInfo } from '../models/admin.models';
import { WebSocketConnectionService } from './graphql.provider';

// GraphQL Subscriptions
const PROCESSOR_STATUS_SUBSCRIPTION = gql`
  subscription OnProcessorStatusUpdated($moduleKey: String) {
    onProcessorStatusUpdated(moduleKey: $moduleKey) {
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

const CHECKPOINT_SUBSCRIPTION = gql`
  subscription OnCheckpointUpdated($moduleKey: String) {
    onCheckpointUpdated(moduleKey: $moduleKey) {
      moduleKey
      checkpoint {
        processorId
        lastPosition
        updatedAt
      }
    }
  }
`;

const DEAD_LETTER_SUBSCRIPTION = gql`
  subscription OnDeadLetterChanged($moduleKey: String) {
    onDeadLetterChanged(moduleKey: $moduleKey) {
      moduleKey
      deadLetter {
        id
        processorId
        eventId
        eventType
        errorMessage
        attemptCount
        failedAt
      }
      changeType
    }
  }
`;

const SYSTEM_INFO_SUBSCRIPTION = gql`
  subscription OnSystemInfoUpdated($moduleKey: String) {
    onSystemInfoUpdated(moduleKey: $moduleKey) {
      moduleKey
      info {
        moduleKey
        globalPosition
        processorCount
        deadLetterCount
        readOnlyMode
      }
    }
  }
`;

export interface ProcessorStatusUpdate {
  moduleKey: string;
  processor: ProcessorStatus;
}

export interface CheckpointUpdate {
  moduleKey: string;
  checkpoint: Checkpoint;
}

export interface DeadLetterUpdate {
  moduleKey: string;
  deadLetter: DeadLetter;
  changeType: 'Added' | 'Removed';
}

export interface SystemInfoUpdate {
  moduleKey: string;
  info: SystemInfo;
}

@Injectable({ providedIn: 'root' })
export class AdminSubscriptionService {
  private readonly apollo = inject(Apollo);
  private readonly wsConnection = inject(WebSocketConnectionService);

  /** Exposes the WebSocket connection state */
  readonly connected = this.wsConnection.connected;

  /**
   * Subscribe to processor status updates.
   */
  subscribeToProcessorStatus(moduleKey?: string): Observable<ProcessorStatusUpdate> {
    return this.createSubscription<{ onProcessorStatusUpdated: ProcessorStatusUpdate }>(
      PROCESSOR_STATUS_SUBSCRIPTION,
      { moduleKey }
    ).pipe(map(data => data.onProcessorStatusUpdated));
  }

  /**
   * Subscribe to checkpoint updates.
   */
  subscribeToCheckpoints(moduleKey?: string): Observable<CheckpointUpdate> {
    return this.createSubscription<{ onCheckpointUpdated: CheckpointUpdate }>(
      CHECKPOINT_SUBSCRIPTION,
      { moduleKey }
    ).pipe(map(data => data.onCheckpointUpdated));
  }

  /**
   * Subscribe to dead letter changes.
   */
  subscribeToDeadLetters(moduleKey?: string): Observable<DeadLetterUpdate> {
    return this.createSubscription<{ onDeadLetterChanged: DeadLetterUpdate }>(
      DEAD_LETTER_SUBSCRIPTION,
      { moduleKey }
    ).pipe(map(data => data.onDeadLetterChanged));
  }

  /**
   * Subscribe to system info updates.
   */
  subscribeToSystemInfo(moduleKey?: string): Observable<SystemInfoUpdate> {
    return this.createSubscription<{ onSystemInfoUpdated: SystemInfoUpdate }>(
      SYSTEM_INFO_SUBSCRIPTION,
      { moduleKey }
    ).pipe(map(data => data.onSystemInfoUpdated));
  }

  private createSubscription<T>(query: ReturnType<typeof gql>, variables: Record<string, unknown>): Observable<T> {
    return this.apollo.subscribe<T>({ query, variables }).pipe(
      map(result => {
        if (!result.data) {
          throw new Error('No data received');
        }
        return result.data;
      }),
      retry({
        delay: (_, retryCount) => {
          const delay = Math.min(1000 * Math.pow(2, retryCount), 30000);
          console.log(`[AdminSubscription] Reconnecting in ${delay}ms`);
          return timer(delay);
        }
      })
    );
  }
}
