// Runs after `astro build`: fails the build if dist/index.html holds an inline
// script whose hash vercel.json's Content-Security-Policy does not allow. The
// page has one on purpose (the theme snippet in Base.astro, which must run
// before first paint); anything else inline would be blocked in production.

import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

const html = readFileSync(new URL('../dist/index.html', import.meta.url), 'utf8');
const vercel = JSON.parse(readFileSync(new URL('../vercel.json', import.meta.url), 'utf8'));
const csp = vercel.headers
  .flatMap((rule) => rule.headers)
  .find((header) => header.key === 'Content-Security-Policy').value;

const inline = [...html.matchAll(/<script(?![^>]*\ssrc=)[^>]*>([\s\S]*?)<\/script>/g)].map((match) => match[1]);
const missing = inline
  .map((body) => `'sha256-${createHash('sha256').update(body, 'utf8').digest('base64')}'`)
  .filter((hash) => !csp.includes(hash));

if (missing.length) {
  console.error(`check-csp: ${missing.length} inline script(s) not allowed by vercel.json: ${missing.join(' ')}`);
  console.error('Add the hash to script-src in vercel.json, or move the script to a file.');
  process.exit(1);
}
console.log(`check-csp: ${inline.length} inline script(s), all allowed`);
