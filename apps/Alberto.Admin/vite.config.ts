import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5174,
    proxy: {
      "/graphql": {
        target: "http://localhost:5163",
        changeOrigin: true,
        ws: true, // WebSocket proxy for GraphQL subscriptions
      },
    },
  },
});
