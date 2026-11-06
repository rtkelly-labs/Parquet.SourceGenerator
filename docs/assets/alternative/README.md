# Alternative Logo Design — Hexagon & Circuit Mark

This directory preserves the historical hexagon-and-circuit mark direction, updated with the modern deterministic SVG pipeline from `shared_utilities.image_drift`.

## 1. Overview & Heritage

The project originally began with this artwork:
- **Reference Raster**: [`hexagon-reference.jpg`](./hexagon-reference.jpg) (1024×1024 master).
- **Composition**: A glowing cyan/violet hexagon frame enclosing warm wooden herringbone/rhombille parquet planks, seam-locked circuit traces, an outward socket notch, and developer emblems (`{}` and `</>`).

Historically, hand-measuring and manually coding this mark in [`rhombille_builder.py`](https://github.com/rtkelly13/shared-utilities/blob/main/src/shared_utilities/image_drift/rhombille_builder.py) proved challenging, achieving only **27.77% SSIM** and **23.01% RMSE**. As a result, the mark was parked on the `logo/alternative-hexagon-mark` branch and the project adopted the synthetic 3-column / 5-column mark in [`../logo.svg`](../logo.svg).

---

## 2. Pipeline Modernization & Empirical Results

Using the upgraded vectorization and RDP polygon simplification pipeline in `image-reconcile`:
- **Vector Output**: [`hexagon-vectorized.svg`](./hexagon-vectorized.svg) (186 KB clean CAD polygon SVG with RDP regularization $\epsilon = 1.2$ px).
- **Comparative Diagnostic Sheet**: [`hexagon-diagnostic-sheet.png`](./hexagon-diagnostic-sheet.png) (`[Reference | Rendered SVG | Diff Heatmap]`).

### Benchmark Comparison

| Metric | Hand-Coded Builder (`rhombille_reference`) | **Modernized Pipeline (`hexagon-vectorized.svg`)** | Net Improvement |
| :--- | :---: | :---: | :---: |
| **Full-Frame RMSE** | 23.01% | **4.53%** | **$5\times$ error reduction** |
| **Structural Similarity (SSIM)** | 27.77% | **93.52%** | **+65.75% fidelity** |
| **Peak Signal-to-Noise (PSNR)** | 12.8 dB | **26.9 dB** | **+14.1 dB cleaner signal** |
| **Top-Right Socket Drift** | 24.10% | **6.63%** | Crisp socket ring & dot |
| **Brace Badge (`{}`) Drift** | 36.81% | **6.73%** | Readable code glyphs |
| **Angle Badge (`</>`) Drift** | 32.58% | **6.12%** | Restored trace geometry |
| **Center Hub Drift** | 32.92% | **5.88%** | Preserved wood plank contrasts |

---

## 3. Brand & Architectural Trade-offs

While the modernized vector solves the reproduction quality problem, the architectural rationale for adopting the columnar mark in [`../logo.svg`](../logo.svg) remains valid:

1. **Semantic Fit**: The columnar mark directly communicates Apache Parquet's core abstraction (*columnar arrays*) and source generation lineage (*forking commit graph*). The hexagon mark reads as an isometric box / package.
2. **1-Bit Thresholding**: The columnar mark cleanly reduces to high-contrast monochrome, whereas the hexagon mark's circuit traces share luminance values with the wood grain.
3. **Small Sizes (16–32px)**: The columnar mark scales into distinct favicons, whereas the intricate hexagon interior merges into a beige block at favicon dimensions.

Both assets are maintained: [`../logo.svg`](../logo.svg) as the official primary brand identity, and [`hexagon-vectorized.svg`](./hexagon-vectorized.svg) as a fully realized, high-fidelity alternative.
