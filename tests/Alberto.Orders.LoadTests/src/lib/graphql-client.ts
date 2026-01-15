import http from 'k6/http';
import { check } from 'k6';
import { getGraphQLUrl } from '../config/environments';
import { getNextTenant } from '../config/tenants';

export interface GraphQLRequest<TVariables = Record<string, unknown>> {
  query: string;
  variables?: TVariables;
  operationName?: string;
}

export interface GraphQLError {
  message: string;
  path?: string[];
  extensions?: Record<string, unknown>;
}

export interface GraphQLResponse<TData = unknown> {
  data?: TData;
  errors?: GraphQLError[];
}

export interface ExecuteOptions {
  /** Override tenant ID (defaults to round-robin) */
  tenantId?: string;
  /** Additional tags for metrics */
  tags?: Record<string, string>;
  /** Whether to run K6 checks on the response (default: true) */
  checkResponse?: boolean;
  /** Operation name override (extracted from query if not provided) */
  operationName?: string;
}

/**
 * Extract operation name from a GraphQL query string.
 */
function extractOperationName(query: string): string {
  const match = query.match(/(?:query|mutation|subscription)\s+(\w+)/);
  return match ? match[1] : 'unknown';
}

/**
 * Execute a GraphQL operation against the API.
 * Automatically includes X-Tenant-Id header using round-robin distribution.
 */
export function executeGraphQL<TData = unknown, TVariables = Record<string, unknown>>(
  request: GraphQLRequest<TVariables>,
  options: ExecuteOptions = {}
): GraphQLResponse<TData> {
  const url = getGraphQLUrl();
  const { tenantId, tags = {}, checkResponse = true, operationName } = options;

  // Get tenant ID (use provided or get next from round-robin)
  const tenant = tenantId ?? getNextTenant();

  // Extract or use provided operation name
  const operation = operationName ?? request.operationName ?? extractOperationName(request.query);

  // Combine tags for metrics
  const allTags = {
    operation,
    tenant,
    ...tags,
  };

  const response = http.post(
    url,
    JSON.stringify({
      query: request.query,
      variables: request.variables,
      operationName: request.operationName,
    }),
    {
      headers: {
        'Content-Type': 'application/json',
        'Accept': 'application/json',
        'X-Tenant-Id': tenant,
      },
      tags: allTags,
    }
  );

  // Parse response
  let result: GraphQLResponse<TData>;
  try {
    result = JSON.parse(response.body as string) as GraphQLResponse<TData>;
  } catch {
    result = { errors: [{ message: 'Failed to parse response body' }] };
  }

  // Run checks if enabled
  if (checkResponse) {
    check(response, {
      'status is 200': (r) => r.status === 200,
      'has data or errors': () => result.data !== undefined || result.errors !== undefined,
      'no GraphQL errors': () => !result.errors || result.errors.length === 0,
    });
  }

  return result;
}

/**
 * Execute a GraphQL mutation.
 */
export function executeMutation<TData = unknown, TVariables = Record<string, unknown>>(
  mutation: string,
  variables: TVariables,
  options: ExecuteOptions = {}
): GraphQLResponse<TData> {
  return executeGraphQL<TData, TVariables>({ query: mutation, variables }, options);
}

/**
 * Execute a GraphQL query.
 */
export function executeQuery<TData = unknown, TVariables = Record<string, unknown>>(
  query: string,
  variables?: TVariables,
  options: ExecuteOptions = {}
): GraphQLResponse<TData> {
  return executeGraphQL<TData, TVariables>({ query, variables }, options);
}

/**
 * Check if a GraphQL response was successful (no errors).
 */
export function isSuccess<T>(response: GraphQLResponse<T>): boolean {
  return !response.errors || response.errors.length === 0;
}

/**
 * Get data from response or throw if errors.
 */
export function getData<T>(response: GraphQLResponse<T>): T | undefined {
  if (response.errors && response.errors.length > 0) {
    console.error(`GraphQL errors: ${JSON.stringify(response.errors)}`);
    return undefined;
  }
  return response.data;
}
