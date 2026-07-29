import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "path";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  server: {
    // Aspire's AddNpmApp(...).WithHttpEndpoint(port: 5174, env: "PORT") puts the DCP
    // proxy on 5174 and passes the port Vite should actually bind in PORT. Hardcoding
    // 5174 here made Vite collide with that proxy, silently fall back to 5175, and
    // leave DCP forwarding to a dead port — the dev server looked up but served nothing.
    // Fall back to 5174 for a bare `npm run dev` outside Aspire.
    port: Number(process.env.PORT ?? 5174),
    // Fail loudly instead of drifting to another port if this one is taken.
    strictPort: true,
  },
});
