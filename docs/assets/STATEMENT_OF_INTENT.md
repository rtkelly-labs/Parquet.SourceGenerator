# Parquet.SourceGenerator Logo — Statement of Intent

The logo is generated, not drawn. Rebuild it with:

```bash
uv run image-reconcile build        # docs/assets/logo.svg
uv run image-reconcile favicon      # docs/assets/favicon/
```

Running either twice produces byte-identical output.

---

## 1. The mark

Two ideas carried by one composition:

| Element | Meaning | Implementation |
| :--- | :--- | :--- |
| **Three columns** | Parquet is a *columnar* format. | `parquet_blocks()` |
| **Inverted negative space** | Adjacent columns are offset by half a pitch, so where one has a block its neighbour has a void. That complementary alternation makes the field read as a laid parquet floor rather than as bars. | half-pitch offset per column |
| **Forking commit graph** | A source generator emits code; the graph is that lineage. A trunk rises from the footer at bottom left and branches cascade rightward, each ending in its own node. | `fork_graph()` |
| **Footer bar** | Parquet files end in a footer holding the schema and row-group metadata. It is also what roots the trunk. | `column_footer()` |
| **Cyan → violet gradient** | House palette, one global ramp across the canvas. | `mark-grad` |

The graph is cased in the card colour before the bright trace is drawn, so it reads as carved
*through* the field rather than laid on top of it.

> [!NOTE]
> **Branches cascade.** Each branch leaves the previous one at the point where that branch turned
> vertical. Forking every branch off a fixed height in its left-hand neighbour puts the later forks
> where that neighbour has no line yet, and the branch appears to start in mid-air. A test walks the
> branches in order and requires each to attach to geometry that already exists.

---

## 2. Reproduction guarantees

Enforced by `tests/test_image_drift_columns.py`:

* **Byte-identical output** for the same variant across runs.
* **No `<text>` or `font-family`** — nothing depends on an installed font.
* **No `<mask>` and no `clip-path`** — Cairo/librsvg-based rasterisers (`rsvg-convert`, Inkscape CLI)
  match browsers. The only filter is the glow, which degrades gracefully: drop it and the underlying
  geometry still draws.
* **Accessible root element**: `<title>`, `<desc>`, `role="img"`, `aria-label`, no fixed pixel size
  (the viewBox carries the ratio), no unused namespace declarations.
* **Survives a one-colour threshold.** The mark uses `mark-grad`, a lighter ramp than the circuit
  gradient: filled with the full cyan-to-magenta sweep, the violet end vanished entirely under a
  1-bit threshold, which is what a one-colour reproduction does.
* **Every lane ends in its own node**, all at the same height.
* **45° branch diagonals** — one lane of rise per lane of run.

### Small sizes

Below roughly 48px the full mark loses its field detail, so the favicon is a separate variant with
coarser blocks and a heavier trace. Rasters are downsampled from one 512px master with Lanczos,
because rendering tiny sizes directly makes the browser scale the glow badly. See
[`favicon/README.md`](./favicon/README.md).

> [!WARNING]
> At 16px the mark still reads as a block — a fork over a segmented field is a lot of information
> for 256 pixels. If a true 16px icon is needed, the fork alone (no parquet field) is a small
> variant change.

---

## 3. Palette

* **Card**: `#1d222d`, border `#2b3140`, inset 32 on all four sides so it is square and concentric
  with the mark
* **Mark gradient** (`mark-grad`): `#22e6f7` → `#3ec8ec` (45%) → `#8b6cf0` (60%) → `#c46ce0`
* **Circuit gradient** (`circuit-global-grad`, used by the preserved hexagon mark):
  `#00f2fe` → `#00b4d8` (35%) → `#7209b7` (65%) → `#b5179e`

Both use `gradientUnits="userSpaceOnUse"` so they span the whole canvas. Under the default
`objectBoundingBox` units every shape gets its own private ramp, and a zero-width vertical line
degenerates to a flat dark stroke.

---

## 4. The earlier direction, preserved

The project began with a different mark: a rhombille or herringbone tiling inside a glowing hexagon
frame, with a seam-locked circuit network, a notched socket dot and etched `{}` / `</>` emblems,
reconstructed by measurement from the original generated artwork. It is kept as a distinct builder
rather than deleted:

```bash
uv run image-reconcile build \
    -B ../shared-utilities/src/shared_utilities/image_drift/rhombille_builder.py \
    --variant rhombille_reference --svg /tmp/hex.svg
```

It shares every geometry helper with the adopted mark, so fixes to the common parts apply to both.
Its measured constants — frame radius 186.2 fitted per edge, wood radius 173, the socket dot
projected onto the frame edge — are documented in that module.

That artwork, its rationale, and the reasons the direction was not adopted live on the long-lived
`logo/alternative-hexagon-mark` branch, as `docs/assets/alternative/hexagon-reference.jpg`. It is
byte-identical to the copy packaged inside the builder, which is what the drift tooling reads, so
`main` does not carry a second copy.

---

## 5. Tooling

* **Builder (adopted)**: `shared_utilities.image_drift.svg_builder`
* **Builder (preserved)**: `shared_utilities.image_drift.rhombille_builder`
* **Shared imaging**: `imaging.py` (rendering, cropping, contact sheets), `metrics.py` (drift scoring)
* **CLI**: `image-reconcile` — `build`, `favicon`, `compare`, `extract-feature`, `contact-sheet`,
  `build-preview`
* **Templates**: Jinja2 in `templates/`, rendered to a self-contained `preview.html`

```bash
uv run image-reconcile build --list-variants
uv run image-reconcile build-preview --mode choose     # selection gallery, no scores
uv run image-reconcile contact-sheet -f bond_seam -o /tmp/sheet.png
```

Preview output lands in the tool repo's gitignored `temp/logo-preview/`, never in `docs/` —
`preview.html` inlines every image as a data URI and runs to megabytes.

> [!IMPORTANT]
> **Drift is a diagnostic, not the design target.** The adopted mark is not derived from the
> reference artwork at all, so its drift score is meaningless. `build-preview` reports scores but leaves the target SVG
> alone; choose deliberately with `build --variant <key>`.
