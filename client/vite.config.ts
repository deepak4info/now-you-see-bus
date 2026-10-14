import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
import { fileURLToPath, URL } from "node:url";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) },
  },
  server: {
    host: "0.0.0.0",
    port: 4317,
    proxy: {
      "/api": { target: "http://127.0.0.1:5088", changeOrigin: true },
      "/hubs": { target: "http://127.0.0.1:5088", ws: true, changeOrigin: true },
    },
  },
});
