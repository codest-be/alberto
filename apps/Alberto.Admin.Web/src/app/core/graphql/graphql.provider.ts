import { inject, Provider } from '@angular/core';
import { InMemoryCache, split } from '@apollo/client/core';
import { GraphQLWsLink } from '@apollo/client/link/subscriptions';
import { HttpLink } from 'apollo-angular/http';
import { getMainDefinition } from '@apollo/client/utilities';
import { createClient, Client } from 'graphql-ws';
import { APOLLO_OPTIONS, Apollo } from 'apollo-angular';
import { HttpClient } from '@angular/common/http';
import { Kind, OperationTypeNode } from 'graphql';

let wsClient: Client | null = null;

function createWsClient(url: string): Client {
  return createClient({
    url,
    connectionParams: {},
    retryAttempts: Infinity,
    retryWait: async (retries) => {
      // Exponential backoff: 1s, 2s, 4s, 8s, max 30s
      const delay = Math.min(1000 * Math.pow(2, retries), 30000);
      console.log(`[Apollo] WebSocket reconnecting in ${delay}ms (attempt ${retries + 1})`);
      await new Promise((resolve) => setTimeout(resolve, delay));
    },
    shouldRetry: () => true,
    on: {
      connected: () => console.log('[Apollo] WebSocket connected'),
      closed: (event) => console.log('[Apollo] WebSocket closed', event),
      error: (error) => console.error('[Apollo] WebSocket error', error),
    },
  });
}

function apolloOptionsFactory() {
  const httpClient = inject(HttpClient);

  // WebSocket URL - use relative path through proxy
  const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  const wsUrl = `${wsProtocol}//${window.location.host}/graphql`;

  console.log('[Apollo] Initializing with WebSocket URL:', wsUrl);

  // HTTP link for queries and mutations
  const httpLink = new HttpLink(httpClient).create({
    uri: '/graphql',
  });

  // Create WebSocket client (singleton to avoid multiple connections)
  if (!wsClient) {
    wsClient = createWsClient(wsUrl);
  }

  // WebSocket link for subscriptions
  const wsLink = new GraphQLWsLink(wsClient);

  // Split link: use WebSocket for subscriptions, HTTP for everything else
  const link = split(
    ({ query }) => {
      const definition = getMainDefinition(query);
      return (
        definition.kind === Kind.OPERATION_DEFINITION &&
        definition.operation === OperationTypeNode.SUBSCRIPTION
      );
    },
    wsLink,
    httpLink
  );

  return {
    link,
    cache: new InMemoryCache(),
    defaultOptions: {
      watchQuery: {
        fetchPolicy: 'network-only' as const,
      },
    },
  };
}

export const graphqlProviders: Provider[] = [
  Apollo,
  {
    provide: APOLLO_OPTIONS,
    useFactory: apolloOptionsFactory,
  },
];
