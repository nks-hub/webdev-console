# Repository Guidelines

## Project Structure & Module Organization

This repository contains NKS WebDev Console, a .NET 9 daemon/CLI with an Electron + Vue frontend. Backend projects live under `src/daemon/`: `Daemon` is the ASP.NET Core REST service, `Core` holds shared models and services, `Cli` builds `wdc`, and `Plugin.SDK` defines plugin contracts. The desktop UI is in `src/frontend/`, with renderer code in `src`, Electron entry points in `electron`, assets in `build` and `public`, and Playwright tests in `tests/playwright`. xUnit projects are under root `tests/`. Side services live in `services/`.

## Build, Test, and Development Commands

- `dotnet build WebDevConsole.sln -c Release` builds the daemon, CLI, core library, SDK, and tests.
- `dotnet test WebDevConsole.sln` runs the .NET xUnit suite.
- `dotnet run --project src/daemon/NKS.WebDevConsole.Daemon` starts the daemon locally.
- `cd src/frontend && npm install && npm run dev` starts Electron with hot reload.
- `cd src/frontend && npm run build` builds the Electron/Vue app.
- `cd src/frontend && npm run type-check` runs `vue-tsc --noEmit`.
- `cd src/frontend && npm test` runs Playwright UI tests.
- `cd services/mcp-server && npm install && npm run build && npm test` builds and tests the MCP server.

On Windows, `run.cmd` starts the daemon and frontend together; use `--daemon-only`, `--frontend-only`, or `--no-build`.

## Coding Style & Naming Conventions

C# uses nullable-aware .NET 9 patterns, PascalCase for public types/members, camelCase for locals/parameters, and `*Tests.cs` for test files. Warnings are errors via `src/daemon/Directory.Build.props`. TypeScript/Vue uses strict type checking, Vue 3 composition patterns, camelCase for variables/functions, and PascalCase for components. Do not edit generated outputs such as `bin/`, `obj/`, `dist-electron/`, `release/`, or `node_modules/`.

## Testing Guidelines

Add or update focused tests with behavior changes. Backend tests belong in `tests/NKS.WebDevConsole.*.Tests` and use names such as `SiteManagerTests`. Frontend specs live in `src/frontend/tests/playwright/tests` and use `*.spec.ts`. MCP tests use Vitest with `*.test.ts`. Run the nearest test first, then broader suites before submitting.

## Commit & Pull Request Guidelines

Recent history follows Conventional Commits, for example `fix(cloudflare): localize DNS table columns`. Use `type(scope): short desc`, English only, single-line header preferred. Never add AI/tool attribution, signatures, or co-author lines. Branch names, commit messages, PR titles, PR bodies, review comments, and merge metadata must stay neutral and project-focused. Pull requests should describe the change, list verification commands, link issues, and include screenshots for UI changes.

## Agent Workflow Notes

Before cross-repo, infrastructure, CI, or architecture work, load MCP memory for WDC context and read `CLAUDE.md` if present; fall back to `C:\work\sources\CLAUDE.md`. The active master roadmap is `docs/plans/2026-05-04-wdc-master-refactor-redesign-plan.md`; related plans include `docs/plans/2026-05-04-ui-ux-refactor-redesign.md`, `docs/plans/2026-05-04-postgresql-platform-support.md`, and `docs/plans/2026-05-04-binaries-catalog-ci.md`. Do not run `tools/pre-push-check.sh` autonomously because prior WDC sessions observed interactive prompts; run the individual gates instead: `npx vue-tsc --noEmit`, `npx playwright test --reporter=line`, `npx electron-vite build`, plus relevant .NET tests. Do not start privileged daemon, hosts, firewall, certificate, or lifecycle checks as a normal local process; use CI or an approved noninteractive elevated path for admin-only verification.

## Security & Configuration Tips

Do not commit local secrets, daemon port/token files, certificates, packaged installers, or machine-specific configuration. Destructive MCP and database operations require explicit confirmation; preserve that pattern in new tools and APIs.
