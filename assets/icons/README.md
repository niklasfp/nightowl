# Owl icon rasters

Generated raster versions of the NightOwl mark. The **single source of truth is
[`site/favicon.svg`](../../site/favicon.svg)** (per [AGENTS.md](../../AGENTS.md) it is shared by
the site hero and the README header) — never edit these PNGs by hand; regenerate them from the
SVG after any change to the mark:

```bash
for n in 16 24 32 48 64 128 256; do
  npx --yes @resvg/resvg-js-cli --fit-width $n site/favicon.svg assets/icons/owl-$n.png
done
```

Consumers of these rasters (regenerate those too when the mark changes):

- `src/NightOwl/icon.png` — the 128×128 nupkg icon (nuget.org rejects SVG), same pipeline.
- `src/NightOwl/icon.ico` — the Windows executable icon; all seven sizes packed as PNG-compressed
  ICO layers.
