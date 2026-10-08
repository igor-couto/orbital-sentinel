import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: process.env.API_PROXY_TARGET ?? "http://localhost:5080",
      },
      "/hubs": {
        target: process.env.API_PROXY_TARGET ?? "http://localhost:5080",
        ws: true,
      },
    },
  },
  build: { sourcemap: false },
});
