# Favicon set

Generated, not hand-edited. Rebuild with:

```bash
uv run image-reconcile favicon
```

Below roughly 48px the full [`logo.svg`](../logo.svg) loses its emblems to smudging and its
circuit network to mud, so this is a distinct builder variant — coarser cells, trunk-only
circuit, no emblems, heavier lines — rather than the same drawing scaled down. See
[`STATEMENT_OF_INTENT.md`](../STATEMENT_OF_INTENT.md).

| File | Use |
| :--- | :--- |
| `favicon.svg` | Modern browsers; scales to any size |
| `favicon.ico` | Legacy fallback, contains 16/32/48px |
| `favicon-16.png`, `favicon-32.png`, `favicon-48.png` | Explicit raster sizes |
| `apple-touch-icon.png` | 180px, iOS home screen |
| `favicon-512.png` | PWA manifest / large raster contexts |

Rasters are downsampled from a single 512px master with Lanczos filtering, because rendering
tiny sizes directly makes the browser scale the glow badly.

## Wiring it up

There is currently no generated HTML site — `docs/` is plain Markdown, which has no `<head>`
to attach these to. When a site exists (DocFX, mkdocs, or similar), add:

```html
<link rel="icon" href="/assets/favicon/favicon.svg" type="image/svg+xml">
<link rel="icon" href="/assets/favicon/favicon.ico" sizes="48x48">
<link rel="apple-touch-icon" href="/assets/favicon/apple-touch-icon.png">
```

Until then these files are unused. That is deliberate — they are ready rather than missing.
