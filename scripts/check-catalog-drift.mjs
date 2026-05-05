#!/usr/bin/env node
/**
 * Catalog API contract drift checker.
 *
 * Fetches ``openapi.json`` from a pinned ``wdc-catalog-api`` GitHub
 * release (or a local file via --spec) and verifies the endpoints /
 * required fields the C# ``CatalogClient.cs`` depends on are still
 * present with the expected shape. Emits a non-zero exit when the
 * contract drifts so CI can block a breaking merge.
 *
 * Usage (CI):
 *   CATALOG_API_VERSION=0.2.0 node scripts/check-catalog-drift.mjs
 *
 * Usage (local):
 *   node scripts/check-catalog-drift.mjs --spec /path/to/openapi.json
 */

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(__dirname, "..");

// Endpoints + DTOs the C# daemon consumes. Add to this list when
// ``CatalogClient.cs`` grows a new dependency.
const REQUIRED_PATHS = [
  "/healthz",
  "/api/v1/catalog",
  "/api/v1/catalog/{app_name}",
  "/api/v1/sync/config",
  "/api/v1/sync/config/{device_id}",
];

// Wire-format fields on ``CatalogDocument`` / related DTOs. Values are
// snake_case because ``CatalogClient.cs`` serializes with
// ``JsonNamingPolicy.SnakeCaseLower``.
const REQUIRED_FIELDS_BY_SCHEMA = {
  TokenResponse: ["token", "email"],
  ConfigSyncEntry: ["device_id", "updated_at", "payload"],
  ConfigSyncUploadRequest: ["device_id", "payload"],
};

function fail(msg) {
  console.error(`[drift-check] ${msg}`);
  process.exitCode = 1;
}

function ok(msg) {
  console.log(`[drift-check] ${msg}`);
}

async function loadSpec(args) {
  const explicit = args.indexOf("--spec");
  if (explicit !== -1 && args[explicit + 1]) {
    const file = args[explicit + 1];
    ok(`Loading spec from ${file}`);
    return JSON.parse(fs.readFileSync(file, "utf-8"));
  }
  const version = process.env.CATALOG_API_VERSION;
  if (!version) {
    fail("CATALOG_API_VERSION not set (and no --spec passed).");
    fail("Either export CATALOG_API_VERSION=x.y.z or pass --spec path.");
    process.exit(2);
  }
  const tag = version.startsWith("v") ? version : `v${version}`;
  const url =
    `https://github.com/nks-hub/wdc-catalog-api/releases/download/` +
    `${tag}/openapi.json`;
  ok(`Fetching spec: ${url}`);
  const res = await fetch(url);
  if (!res.ok) {
    fail(`Failed to fetch ${url}: ${res.status} ${res.statusText}`);
    process.exit(2);
  }
  return await res.json();
}

function checkPaths(spec) {
  const present = spec.paths ?? {};
  for (const p of REQUIRED_PATHS) {
    if (!(p in present)) {
      fail(`Missing required path: ${p}`);
    } else {
      ok(`Path present: ${p}`);
    }
  }
}

function checkSchemas(spec) {
  const schemas = spec.components?.schemas ?? {};
  for (const [name, fields] of Object.entries(REQUIRED_FIELDS_BY_SCHEMA)) {
    const schema = schemas[name];
    if (!schema) {
      fail(`Missing schema: ${name}`);
      continue;
    }
    const props = schema.properties ?? {};
    for (const field of fields) {
      if (!(field in props)) {
        fail(`Schema ${name} is missing field '${field}'`);
      }
    }
    ok(`Schema ${name} has required fields [${fields.join(", ")}]`);
  }
}

function checkCSharpStillCompiles() {
  // Light sanity check: the C# client file still exists and mentions
  // the critical DTO class names. Full type-level check needs dotnet
  // build, which the CI's downstream job already runs.
  const clientPath = path.join(
    REPO_ROOT,
    "src/daemon/NKS.WebDevConsole.Daemon/Binaries/CatalogClient.cs",
  );
  if (!fs.existsSync(clientPath)) {
    fail(`Missing CatalogClient.cs at ${clientPath}`);
    return;
  }
  const source = fs.readFileSync(clientPath, "utf-8");
  for (const token of ["CatalogDocument", "ReleaseDoc", "DownloadDoc"]) {
    if (!source.includes(token)) {
      fail(`CatalogClient.cs no longer references '${token}'`);
    }
  }
  ok("CatalogClient.cs references CatalogDocument/ReleaseDoc/DownloadDoc");
}

const args = process.argv.slice(2);
const spec = await loadSpec(args);
checkPaths(spec);
checkSchemas(spec);
checkCSharpStillCompiles();

if (process.exitCode) {
  console.error("[drift-check] FAILED — catalog-api contract has drifted.");
  process.exit(1);
}
ok("All contract checks passed.");
