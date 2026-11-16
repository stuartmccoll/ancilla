# Ancilla favicon suite

Built from `ancilla-symbol-clean.svg` (the smooth, geometric vector redraw).

## Install
Copy the root favicon files into your website's `public/` folder (site root). Add the contents of `html-head-snippet.html` inside the page `<head>`.

If the site is hosted at a URL subpath, edit the absolute `/...` URLs in the HTML and `site.webmanifest`.

## Key files
- `favicon.svg` — simplified eye/orb geometry for tiny browser tabs; crisp vector (light variant).
- `favicon.ico` — classic multi-size ICO (16, 32, 48).
- `favicon-16x16.png`, `favicon-32x32.png`, `favicon-48x48.png` — deliberately simplified small-size icons. The 16px PNG/ICO entry is hand pixel-aligned for maximum legibility.
- `favicon-64x64.png` through `favicon-512x512.png` — full Ancilla symbol on the light tile.
- `ancilla-icon.svg` — full Ancilla symbol on a square tile.
- `ancilla-symbol-transparent.svg` — unmodified cleaned source symbol, no tile.
- `ancilla-symbol-transparent-512.png` — transparent PNG version of the full symbol.
- `apple-touch-icon.png` — 180 x 180 Apple icon.
- `android-chrome-192x192.png`, `android-chrome-512x512.png` — PWA icons.
- `android-maskable-512x512.png` — PWA adaptive-safe artwork with extra padding.
- `site.webmanifest` — example manifest; update name, start URL and scope as needed.
- `dark/` — alternate dark-background versions; not referenced by the default manifest/snippet.

The main files use a subtle pale background (#F4F8FB), charcoal/steel geometry and blue eye. All PNGs are RGBA; background tiles are intentionally opaque. Exact transparent artwork is separately provided.

The tiny version uses a simplified representation of the same orb, with enlarged center eye and reduced details for reliable 16–48px legibility. Larger icons use the actual original cleaned SVG paths.
