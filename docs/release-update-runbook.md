# Release & Update Runbook

This is the current source of truth for Electron packaging and updater validation.

## Packaging Source Of Truth

- Electron packaging config: `src/frontend/electron-builder.yml`
- Local release smoke verifier: `scripts/verify-electron-release.mjs`
- Tagged release workflow: `.github/workflows/build.yml`
- Windows packaging smoke in CI: `.github/workflows/ci.yml`

## Local Windows Release Smoke

Run from `src/frontend/`:

```powershell
npm run dist
node ..\..\scripts\verify-electron-release.mjs --dir .\release --target win
npm run smoke:packaged:win
```

`npm run smoke:packaged:win` can temporarily replace the repo-local debug daemon from
`src/daemon/NKS.WebDevConsole.Daemon` and restore it afterwards, so the packaged smoke
does not require manual daemon cleanup on a normal developer workstation.

Expected release outputs:

- `release/NKS WebDev Console-<version>-setup-x64.exe`
- `release/NKS WebDev Console-<version>-portable-x64.exe`
- `release/latest.yml`
- `release/*.blockmap`
- `release/win-unpacked/resources/daemon/NKS.WebDevConsole.Daemon.exe`

The packaged smoke step verifies one level deeper than static artifact checks:

- `release/win-unpacked/NKS WebDev Console.exe` starts successfully
- the packaged Electron main process spawns the bundled daemon from `resources/daemon/`
- `%TEMP%\nks-wdc-daemon.port` appears with a valid token
- `/api/status` responds through the packaged runtime
- `POST /api/admin/shutdown` removes the port file again

Latest local verification on `2026-04-11` passed end-to-end with:

```powershell
npm run smoke:packaged:win
```

## Tagged GitHub Release Validation

This is the remaining Phase 8 step. Do it on a real tag push, not from a local dry run.

1. Push a tag like `v0.1.1`.
2. Wait for `Build & Release` to finish on GitHub Actions.
3. Confirm the uploaded Windows assets include:
   - setup exe
   - portable exe
   - `latest.yml`
   - setup `.blockmap`
4. Install the previous released version.
5. Publish the new tag/release assets.
6. Launch the previous installed app and trigger `Check for Updates` from the tray.
7. Confirm the app reports `Downloading <version>` and then `Ready to install <version>`.
8. Install the update and verify the restarted app reports the new version and still spawns the bundled daemon successfully.

## Portable Mode Notes

- Portable mode is enabled by placing `portable.txt` next to the packaged app executable.
- In portable mode, updates are intentionally disabled.
- Runtime data is redirected under `data/wdc/` next to the app.

## Current Known Limitation

Updater plumbing, packaging, bundled-daemon resolution, and release artifact validation are now implemented.
The only remaining open item is proving the full updater flow against an actual tagged GitHub release feed.
