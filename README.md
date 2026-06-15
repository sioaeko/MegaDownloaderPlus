# MegaDownloader+

Clean-room MEGA download manager inspired by the old MegaDownloader workflow.

MegaDownloader+ is an unofficial client and is not affiliated with, endorsed by, or sponsored by MEGA.

This repo does not copy the original application code. The old binary was only used to identify product behavior and broad module boundaries. New implementation work should stay based on public documentation, SDK behavior, and independently written code.

## Project Layout

- `src/MegaDownloaderNext.Core` - domain models, MEGA link parsing, queue orchestration, service contracts.
- `src/MegaDownloaderNext.App` - Windows WPF desktop shell.
- `tests/MegaDownloaderNext.SmokeTests` - dependency-free smoke tests that run with `dotnet run`.
- `docs` - clean-room planning notes.

## Current MVP

- Parse old and new MEGA file/folder link formats.
- Add valid links to a local queue.
- Resolve and download public file links.
- Expand public folder links into individual queued files.
- Preserve folder paths when downloading expanded folder content.
- Run up to two downloads at once and stop active transfers.

## Dependencies

- `MegaApiClient` 1.10.5 is used for public folder expansion and folder-node downloads. It is MIT licensed and documented at https://gpailler.github.io/MegaApiClient/.
- The direct public-file path still lives behind `IMegaClient` / `IDownloadEngine` so it can be replaced with official SDK bindings later.

## Commands

```powershell
dotnet build .\MegaDownloaderNext.sln
dotnet run --project .\tests\MegaDownloaderNext.SmokeTests\MegaDownloaderNext.SmokeTests.csproj
dotnet run --project .\src\MegaDownloaderNext.App\MegaDownloaderNext.App.csproj
.\scripts\Build-Installer.ps1
```

## Publishing Notes

Generated installers, portable zips, verification builds, IDE state, and local scratch probes are intentionally ignored. Keep GitHub source-only unless you are attaching installers to a release.
