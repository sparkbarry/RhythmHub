# AGENTS.md

RhythmHub: a Windows / macOS desktop launcher (C#, .NET 10, Avalonia 12) that installs, updates, verifies, launches
and rolls back rhythm games (MajdataPlay, OurTaikoPlay; both Unity) and the OurTaikoEdit chart editor (Avalonia). The games are built in their own repositories;
this repository holds the launcher, the release tooling and the release pipeline.

The repository was formerly MajdataHub (Tauri) and was renamed from `sparkbarry/MajdataHub` to `sparkbarry/RhythmHub`
on 2026-11-02. The Tauri code is gone; ignore it in the git history before `a3840d7`.

## Read first

| Topic | File |
|---|---|
| Update protocol: S3 layout, signed manifests, local install layout, health check | [docs/update-protocol.md](docs/update-protocol.md) |
| Release infrastructure: AWS, CDN, secrets, publishing, rollback, onboarding a new game | [docs/operations.md](docs/operations.md) (§0 is a quick reference with the rules below in full) |
| Original design brief | Section numbers in code comments (for example "§5") refer to docs/update-protocol.md |

## Layout

```text
src/RhythmHub.Launcher/   Avalonia UI (MVVM, CommunityToolkit.Mvvm), composition root, games.json catalog
src/RhythmHub.Updater/    platform-agnostic core: manifests, signing (Security/), downloads, install, recovery
src/RhythmHub.Platform/   Windows / macOS specifics: paths, launch, process detection, package checks
tests/RhythmHub.Updater.Tests/   xunit v3 on Microsoft.Testing.Platform
tools/RhythmHub.ReleaseTool/     rhythmhub-release: stage, publish, keygen, fake-game
tools/ci/go-live.sh              makes a signed release live on a channel (shared by CI paths)
tools/ci/launcher-upload.sh      uploads the launcher's Velopack output to its self-update feed
.github/workflows/launcher.yml   launcher test + vpk pack; v1.x+ tags publish (docs/operations.md §8)
build/macos/                     launcher entitlements and app icon (RhythmHub.icns)
design/logo/                     logo sources (SVG); export.sh regenerates the .icns, .ico and site favicons
.github/workflows/release-tool.yml  prebuilt single-file rhythmhub-release → "release-tool" GitHub release
tools/fake-game/main.c           native stand-in player for pipeline tests
.github/actions/upload-build/    composite action the game repositories call
.github/workflows/               publish-game.yml (manual publish / rollback), test-pipeline.yml (regression)
infra/aws/                       CloudFormation for the GitHub OIDC role
site/                            static intro page: index.html (zh), en/, ja/ share style.css and main.js; on Vercel
```

## Commands

```bash
dotnet build
dotnet test
dotnet run --project src/RhythmHub.Launcher
dotnet run --project tools/RhythmHub.ReleaseTool -- <stage|publish|keygen|fake-game> ...
```

`TreatWarningsAsErrors` is on; package versions are central in `Directory.Packages.props`.
For local end-to-end runs see the README ("本地模拟 S3"); the launcher honours `RHYTHMHUB_FEED_URL`,
`RHYTHMHUB_INSTALL_ROOT` and, in Debug builds only, `RHYTHMHUB_DEV_PUBLIC_KEY`.

## Rules

1. Never print, cat, echo or paste the manifest signing key (`~/.rhythmhub/*.key`, secret `RHYTHMHUB_SIGNING_KEY`).
   Hand it to tools only by redirection, e.g. `gh secret set RHYTHMHUB_SIGNING_KEY --repo <repo> < ~/.rhythmhub/rhythmhub-2026.key`.
2. Published versions are immutable: never overwrite `{game}/{platform}/{version}/` or `{version}.json`; ship a new version.
3. Go-live order is fixed (files, version manifest, CDN check, channel pointer, CDN check) and lives in `tools/ci/go-live.sh`.
4. Channel pointers are `Cache-Control: no-cache`; everything else is `public, max-age=31536000, immutable`. No CloudFront invalidations.
5. Upload builds with `aws s3 sync --no-follow-symlinks`; symlinks, modes and empty directories travel in the manifest.
6. Only `rhythmhub-release publish` signs. Never hand-edit signed JSON.
7. AWS OIDC trust uses GitHub immutable subjects (`repo:Owner@id/Name@id`); look them up with `gh api repos/<o>/<r>/actions/oidc/customization/sub`.
8. The bucket is public through the CDN: release files only.
9. Deletions only through nightly pruning in `go-live.sh` and the Pipeline test `cleanup` (PipelineTest/ only).
10. After touching the upload action, `go-live.sh`, the release tool or AWS config, run the **Pipeline test** workflow before real games use it.
11. Self-hosted game runners have little disk: the upload action must not install large dependencies there
    (it uses the prebuilt release tool; stage bundles with APFS clones, `cp -c`). See docs/operations.md §7.1.
12. The launcher ships through Velopack, not the game protocol: tag `vX.Y.Z` (>= 1.0.0; v0.x tags are the old Tauri app) to release; never reuse a version.
13. Keep docs/operations.md §0.3 (status) and §3 (secrets table) current when infrastructure changes.

## Conventions

- Code comments, identifiers and commit messages in English (Conventional Commits: `feat:`, `fix:`, `ci:`, `docs:`, `chore:`).
- Launcher UI strings live in src/RhythmHub.Launcher/Resources/Strings.resx (English, neutral) with Strings.zh.resx and
  Strings.ja.resx; add every new string to all three. Game descriptions live in games.json as one `description` object keyed by language (`en` is the fallback). The docs are in Chinese.
- site/ keeps one hand-written page per language; change all three together. Screenshots per language are in site/images/{zh,en,ja}/.
- Match the surrounding code: small sealed classes, records for data, `UpdateException` with an `UpdateErrorCode` for expected failures.
