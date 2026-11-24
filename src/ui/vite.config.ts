import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The built files are embedded into RigControl.exe and served from https://app.rigcontrol/.
export default defineConfig({
  plugins: [react()],
  base: "/",
  build: {
    outDir: "dist",
    emptyOutDir: true,
    assetsInlineLimit: 0,
    sourcemap: false,
    target: "es2022",
  },
  server: {
    port: 5173,
    strictPort: true,
  },
});
