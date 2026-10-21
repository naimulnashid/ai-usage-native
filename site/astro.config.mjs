// @ts-check
import { defineConfig } from 'astro/config';

// Vercel sets VERCEL_PROJECT_PRODUCTION_URL on every build, so canonical and
// link-preview URLs always name the production site; locally they name the
// dev server.
const productionHost = process.env.VERCEL_PROJECT_PRODUCTION_URL;

export default defineConfig({
  site: productionHost ? `https://${productionHost}` : 'http://localhost:4321',
  output: 'static',
  build: {
    // One page: inlined CSS saves a render-blocking request.
    inlineStylesheets: 'always',
  },
  // No CSP <meta> from Astro: its hashes would disable 'unsafe-inline', and the
  // charts set colours through style attributes. vercel.json sends the CSP.
  devToolbar: { enabled: false },
  vite: {
    build: {
      // Never inline a script or asset: the CSP allows scripts from 'self' plus
      // the one inline theme snippet by hash (scripts/check-csp.mjs).
      assetsInlineLimit: 0,
    },
  },
});
