import type { ImageMetadata } from 'astro';

/**
 * The README's screenshots, imported from docs/screenshots so the site and the
 * README always show the same images. tools\Capture-Readme.ps1 regenerates them
 * from invented demo data, each page dark and `-light` from one run.
 */
const files = import.meta.glob<ImageMetadata>('../../../docs/screenshots/*.webp', {
  eager: true,
  import: 'default',
});

export interface Screenshot {
  dark: ImageMetadata;
  light: ImageMetadata;
}

/** A screenshot in both themes, by its base name ("overview-claude"). */
export function screenshot(name: string): Screenshot {
  const find = (file: string) => {
    const hit = Object.entries(files).find(([path]) => path.endsWith(`/${file}.webp`));
    if (!hit) throw new Error(`docs/screenshots/${file}.webp is missing`);
    return hit[1];
  };
  return { dark: find(name), light: find(`${name}-light`) };
}
