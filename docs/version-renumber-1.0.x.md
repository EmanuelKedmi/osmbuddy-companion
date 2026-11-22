# Companion version renumber (patch-only from 1.0.6)

Sagiv’s rule: only the last number moves (`x.x.Y`). Mistaken minor bumps after `1.0.6` are remapped onto a coherent `1.0.x` line. Future `feat` / `fix` commits only cut patches (see `scripts/patch-only-analyzer.cjs`).

## Mapping (old → correct)

| Old tag (wrong minor) | Correct tag | Commit |
| --- | --- | --- |
| `v1.0.6` | `v1.0.6` (unchanged) | `53b52f0` |
| `v1.1.0` | `v1.0.7` | `863e319` |
| `v1.1.1` | `v1.0.8` | `0872bca` |
| `v1.2.0` | `v1.0.9` | `ebc6650` |
| `v1.2.1` | `v1.0.10` | `6b05412` |
| `v1.3.0` | `v1.0.11` | `4954021` |

After this renumber, the GitHub **latest** release is `v1.0.11` (same bits as the old `v1.3.0` build). The next shipped feature or fix is `v1.0.12`.

## Why minors appeared

`.releaserc.json` already said `feat → patch`, but `@semantic-release/commit-analyzer` still applied the angular preset’s `feat → minor` and chose the **higher** type. The custom `scripts/patch-only-analyzer.cjs` replaces that plugin and can only return `patch` or no release.
