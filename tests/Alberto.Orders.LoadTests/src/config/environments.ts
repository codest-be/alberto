export interface Environment {
  baseUrl: string;
  graphqlEndpoint: string;
}

export const environments: Record<string, Environment> = {
  local: {
    baseUrl: 'http://localhost:5180',
    graphqlEndpoint: '/graphql',
  },
  docker: {
    baseUrl: 'http://orders-api:8080',
    graphqlEndpoint: '/graphql',
  },
  staging: {
    baseUrl: 'https://staging-orders.alberto.dev',
    graphqlEndpoint: '/graphql',
  },
  production: {
    baseUrl: 'https://orders.alberto.dev',
    graphqlEndpoint: '/graphql',
  },
};

export function getEnvironment(): Environment {
  const envName = __ENV.ENV || 'local';
  const env = environments[envName];
  if (!env) {
    throw new Error(`Unknown environment: ${envName}. Available: ${Object.keys(environments).join(', ')}`);
  }
  return env;
}

export function getGraphQLUrl(): string {
  const env = getEnvironment();
  return `${env.baseUrl}${env.graphqlEndpoint}`;
}
