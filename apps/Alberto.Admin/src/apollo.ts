import {
  ApolloClient,
  InMemoryCache,
  HttpLink,
  split,
} from "@apollo/client";
import { GraphQLWsLink } from "@apollo/client/link/subscriptions";
import { getMainDefinition } from "@apollo/client/utilities";
import { createClient } from "graphql-ws";

const httpLink = new HttpLink({
  // Vite dev proxy forwards /graphql → Orders API
  uri: "/graphql",
});

const wsLink = new GraphQLWsLink(
  createClient({
    // WebSocket also proxied by Vite
    url: `${window.location.protocol === "https:" ? "wss:" : "ws:"}//${window.location.host}/graphql`,
  }),
);

// Route subscriptions over WebSocket, everything else over HTTP
const splitLink = split(
  ({ query }) => {
    const definition = getMainDefinition(query);
    return (
      definition.kind === "OperationDefinition" &&
      definition.operation === "subscription"
    );
  },
  wsLink,
  httpLink,
);

export const apolloClient = new ApolloClient({
  link: splitLink,
  cache: new InMemoryCache(),
  defaultOptions: {
    watchQuery: { fetchPolicy: "cache-and-network" },
  },
});
