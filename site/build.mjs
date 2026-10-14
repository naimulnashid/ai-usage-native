// Assembles the project site into site/dist for Vercel (Root Directory: site).
//
// The screenshots are copied from docs/screenshots rather than kept here too:
// tools\Capture-Readme.ps1 regenerates that folder, and a second copy would go
// stale the first time it did. Vercel's build sees the whole repository, so
// reaching outside the root directory works there as it does locally.

import { cpSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const SITE = dirname(fileURLToPath(import.meta.url));
const OUT = join(SITE, 'dist');
const SHOTS = join(SITE, '..', 'docs', 'screenshots');
// The repository's social card doubles as the page's link preview (og:image).
const CARD = join(SITE, '..', '.github', 'social-preview.png');

for (const need of [SHOTS, CARD]) {
  if (!existsSync(need)) {
    console.error(`Missing ${need}; the site would render with broken images.`);
    process.exit(1);
  }
}

rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });
for (const f of ['index.html', 'styles.css', 'site.js', 'favicon.svg']) {
  cpSync(join(SITE, f), join(OUT, f));
}
cpSync(CARD, join(OUT, 'social-preview.png'));
cpSync(SHOTS, join(OUT, 'screenshots'), { recursive: true });
console.log(`site built into ${OUT}`);
