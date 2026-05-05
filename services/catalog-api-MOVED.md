# catalog-api has moved

This service was extracted into its own public repository:

**https://github.com/nks-hub/wdc-catalog-api**

## Why

- Zero code coupling with the rest of the monorepo — only a JSON wire contract
- Separate tech stack (Python/FastAPI/Docker vs C#/.NET/Electron)
- Independent release cadence — catalog scraping iterates weekly, desktop app monthly
- Dedicated CI pipeline (ruff + mypy + pytest + pip-audit) instead of cross-stack overhead
- Cleaner contributor surface — backend contributors don't need to clone .NET + Electron

## Contract

The desktop daemon consumes the public JSON at `https://wdc.nks-hub.cz/api/v1/catalog`.
The new repo publishes `openapi.json` as a GitHub Release asset on every
tagged version — this repo's CI pins a catalog schema version via
`CATALOG_API_VERSION` and regenerates C# DTOs, failing the build if the
hand-maintained `CatalogClient.cs` drifts from the spec.

## History

The pre-split monorepo state is preserved under tag
[`catalog-api-pre-split`](https://github.com/nks-hub/webdev-console/releases/tag/catalog-api-pre-split).
The new repo was created via `git filter-repo --subdirectory-filter services/catalog-api`
so all 700+ commits retain their original authors and dates.

## Deployment

The public instance `wdc.nks-hub.cz` is deployed from
`ghcr.io/nks-hub/wdc-catalog-api:latest`. See the new repo's README for
operational runbooks.
