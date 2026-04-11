# NKS WebDev Console — Performance Baselines

**Last run:** 2026-04-11T01:04:54.441Z
**Host:** DESKTOP-HD73V8A (win32/x64, 24× AMD Ryzen 9 7900X3D 12-Core Processor          , 63.2 GB RAM)
**Samples:** 200 sequential / 10 concurrent

## /api/status — sequential latency

| Metric | Value |
|--------|-------|
| samples | 200 |
| min | 0.1 ms |
| p50 | 0.14 ms |
| p95 | 0.23 ms |
| p99 | 0.28 ms |
| max | 0.31 ms |
| mean | 0.15 ms |
| HTTP 200 | 200/200 |

## /api/services — sequential latency

| Metric | Value |
|--------|-------|
| p50 | 55.38 ms |
| p95 | 62.27 ms |
| p99 | 65.52 ms |
| max | 72.8 ms |
| mean | 54.78 ms |

## /api/status — parallel throughput (10 concurrent)

| Metric | Value |
|--------|-------|
| total requests | 200 |
| elapsed | 28.13 ms |
| **RPS** | **7110.3** |
| p50 | 1.2 ms |
| p99 | 3.64 ms |
| HTTP 200 | 200/200 |

## SSE broadcast rate

| Metric | Value |
|--------|-------|
| events received | 3 |
| duration | 5009 ms |
| events/sec | 0.6 |

## Regression budget (warn if exceeded by >20%)

- /api/status p99 < 0.34 ms
- /api/services p99 < 78.62 ms
- /api/status RPS > 5688
- SSE events/sec > 0.48

Run `node scripts/perf-baseline.mjs` to refresh after meaningful daemon changes.
