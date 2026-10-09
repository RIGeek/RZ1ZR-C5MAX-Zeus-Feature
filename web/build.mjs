// SPDX-License-Identifier: GPL-2.0-or-later
// Dependency-free build: inlines src/styles.css into src/hf-amp.js -> dist/hf-amp.js.
// Also refuses raw colours in the stylesheet (Zeus panels must use theme tokens only).
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = dirname(fileURLToPath(import.meta.url));
const css = readFileSync(join(root, 'src/styles.css'), 'utf8');
const js = readFileSync(join(root, 'src/hf-amp.js'), 'utf8');

const raw = css.match(/#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla|oklch|oklab|lab|lch|color)\(/g);
if (raw) {
  console.error('Raw colours are not allowed in styles.css (use theme tokens):', [...new Set(raw)].join(' '));
  process.exit(1);
}
if (!js.includes('/*__STYLES__*/')) {
  console.error('hf-amp.js is missing the /*__STYLES__*/ placeholder');
  process.exit(1);
}

mkdirSync(join(root, 'dist'), { recursive: true });
const out = js.replace('/*__STYLES__*/""', JSON.stringify(css));
writeFileSync(join(root, 'dist/hf-amp.js'), out);
// build-package.ps1 sits beside the .csproj and resolves ui.modules relative to it.
const feature = join(root, '../src/Zeus.Community.HfAmp/ui');
mkdirSync(feature, { recursive: true });
writeFileSync(join(feature, 'hf-amp.js'), out);
console.log(`dist/hf-amp.js ${out.length} bytes`);
