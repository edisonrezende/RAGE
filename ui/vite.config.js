import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// A interface é servida pelo próprio RAGE!.exe (WebView2) em https://rage.app/
// Em `npm run dev` ela roda no navegador com dados simulados (src/lib/mock.js).
export default defineConfig({
  base: "./",
  plugins: [react(), tailwindcss()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    assetsInlineLimit: 0,
    chunkSizeWarningLimit: 1500,
  },
});
