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
      "/api": {
        target: process.env.VITE_API_BASE_URL || "https://fatehabad4info-001-site1.itempurl.com",
        changeOrigin: true,
        secure: false,
      },
      "/hubs": {
        target: process.env.VITE_API_BASE_URL || "https://fatehabad4info-001-site1.itempurl.com",
        ws: true,
        changeOrigin: true,
        secure: false,
      },
    },
  },
});
