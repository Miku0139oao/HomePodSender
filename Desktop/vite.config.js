import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  base: './',
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    // Rust 编译产物会被锁住（EBUSY），不监视 src-tauri / 打包输出，避免开发服务器崩溃
    watch: { ignored: ['**/src-tauri/**', '**/sidecar-publish/**', '**/dist/**', '**/shots/**'] },
  },
  build: { outDir: 'dist', emptyOutDir: true },
});
