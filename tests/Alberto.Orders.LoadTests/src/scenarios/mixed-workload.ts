import { sleep } from 'k6';
import { orderLifecycleScenario, createAndConfirmScenario } from './order-lifecycle';
import { orderWithCancellationScenario } from './order-with-cancellation';
import { readHeavyScenario } from './read-heavy';
import { randomIntBetween } from '../lib/data-generators';

/**
 * Mixed workload scenario simulating realistic production traffic.
 *
 * Distribution:
 * - 40% Read operations (queries)
 * - 35% Complete order lifecycles
 * - 15% Partial lifecycles (create + confirm)
 * - 10% Cancellations
 */
export function mixedWorkloadScenario(): void {
  const roll = randomIntBetween(1, 100);

  if (roll <= 40) {
    // 40% - Read operations
    readHeavyScenario();
  } else if (roll <= 75) {
    // 35% - Complete lifecycle
    orderLifecycleScenario();
  } else if (roll <= 90) {
    // 15% - Create and confirm only
    createAndConfirmScenario();
  } else {
    // 10% - Cancellation flow
    orderWithCancellationScenario();
  }

  // Small random delay to vary timing
  sleep(randomIntBetween(1, 3) / 10);
}

/**
 * Weighted scenario executor for more control over distribution.
 */
export function executeWeightedScenario(weights: {
  lifecycle?: number;
  reads?: number;
  cancellations?: number;
  partial?: number;
}): void {
  const {
    lifecycle = 35,
    reads = 40,
    cancellations = 10,
    partial = 15,
  } = weights;

  const total = lifecycle + reads + cancellations + partial;
  const roll = randomIntBetween(1, total);

  let cumulative = 0;

  cumulative += reads;
  if (roll <= cumulative) {
    readHeavyScenario();
    return;
  }

  cumulative += lifecycle;
  if (roll <= cumulative) {
    orderLifecycleScenario();
    return;
  }

  cumulative += partial;
  if (roll <= cumulative) {
    createAndConfirmScenario();
    return;
  }

  // Remaining - cancellations
  orderWithCancellationScenario();
}

/**
 * Write-heavy scenario - focuses on mutations.
 * Useful for stress testing write paths.
 */
export function writeHeavyScenario(): void {
  const roll = randomIntBetween(1, 100);

  if (roll <= 60) {
    // 60% - Complete lifecycle
    orderLifecycleScenario();
  } else if (roll <= 85) {
    // 25% - Create and confirm
    createAndConfirmScenario();
  } else {
    // 15% - Cancellation
    orderWithCancellationScenario();
  }

  sleep(randomIntBetween(1, 2) / 10);
}

/**
 * Read-only scenario - no mutations.
 * Useful for testing query performance in isolation.
 */
export function readOnlyScenario(): void {
  readHeavyScenario();
}
