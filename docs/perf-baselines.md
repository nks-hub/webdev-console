# NKS WebDev Console — Performance Baselines

**Last run:** 2026-04-11T06:47:52.463Z
**Host:** DESKTOP-HD73V8A (win32/x64, 24× AMD Ryzen 9 7900X3D 12-Core Processor          , 63.2 GB RAM)
**Samples:** 200 sequential / 10 concurrent

## /api/status — sequential latency

| Metric | Value |
|--------|-------|
| samples | 200 |
| min | 0.13 ms |
| p50 | 0.19 ms |
| p95 | 0.27 ms |
| p99 | 0.35 ms |
| max | 0.37 ms |
| mean | 0.2 ms |
| HTTP 200 | 200/200 |

## /api/services — sequential latency

| Metric | Value |
|--------|-------|
| p50 | 94.08 ms |
| p95 | 105.65 ms |
| p99 | 111.4 ms |
| max | 113.41 ms |
| mean | 93.52 ms |

## /api/status — parallel throughput (10 concurrent)

| Metric | Value |
|--------|-------|
| total requests | 200 |
| elapsed | 29.06 ms |
| **RPS** | **6882.1** |
| p50 | 1.27 ms |
| p99 | 3.21 ms |
| HTTP 200 | 200/200 |

## SSE broadcast rate

| Metric | Value |
|--------|-------|
| events received | 6 |
| duration | 5001 ms |
| events/sec | 1.2 |

## Regression budget (warn if exceeded by >20%)

- /api/status p99 < 0.42 ms
- /api/services p99 < 133.68 ms
- /api/status RPS > 5506
- SSE events/sec > 0.96

Run `node scripts/perf-baseline.mjs` to refresh after meaningful daemon changes.
