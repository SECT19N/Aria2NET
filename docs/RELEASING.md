# Releasing

Releases are driven by git tags. Pushing a tag builds, tests, packages and publishes a GitHub Release; nobody edits version numbers inside CI.

## Versioning
- [SemVer](https://semver.org): `MAJOR.MINOR.PATCH`, optional pre-release suffix (`-beta.1`, `-rc.1`).
- The one place the number lives is `<Version>` in `Directory.Build.props`. The build stamps it into the exe (file properties, About page, log line "Aria2NET x.y.z starting").
- The git tag is that number with a `v` in front: `v0.3.0`, `v1.0.0-rc.1`.
- Below 1.0 every release is marked **pre-release** on GitHub (anything can still change). From 1.0 on only tags with a suffix are pre-releases.
- Roadmap versions map to tags: roadmap 0.3 → `v0.3.0`; fixes after that → `v0.3.1`.

## Cutting a release
1. Make sure `master` is green on GitHub (the `build` workflow).
2. Set `<Version>` in `Directory.Build.props` (e.g. `0.3.0`) and commit it.
3. Tag and push:
   ```
   git tag v0.3.0
   git push origin master v0.3.0
   ```
4. The `release` workflow then:
   - **verify**: the tag must look like `vX.Y.Z[-suffix]` and `X.Y.Z` must equal `<Version>` (`tools/check-release-tag.ps1`), then build + test.
   - **package**: `tools/package.ps1` for `win-x64` and `win-arm64`. Each zip is self-contained (no .NET or Windows App SDK install needed) and bundles `aria2c.exe`.
   - **release**: creates the GitHub Release with generated notes, both zips and a `.sha256` file for each.
5. Edit the generated release notes if you want (they come from commit/PR titles since the previous tag).

A wrong tag (version mismatch, typo) fails in step *verify* before anything is published. If a release was created by mistake, delete the release and the tag (`git push origin :refs/tags/v0.3.0`) and start again.

## Building a package locally
```
pwsh -ExecutionPolicy Bypass -File tools/package.ps1 -Rid win-x64 -Version 0.3.0
```
Output goes to `artifacts/` (git-ignored). `win-arm64` builds on an x64 machine too, but cannot be run there.

## What the zips contain / known limits
- `Aria2NET.exe` plus runtime files, and `tools\aria2\aria2c.exe`. The arm64 zip ships the **x64** aria2 build (there is no official arm64 build); Windows runs it under emulation.
- The exe is **not code signed**, so Windows SmartScreen may warn on first run. Verify downloads against the `.sha256` file. Free signing through the SignPath Foundation OSS program and a winget manifest are planned for 0.9 (see `ROADMAP.md`).
- `dotnet publish` skips the compiled XAML and the app's resource index for unpackaged WinUI apps; `Aria2NET.csproj` has a target that adds them and `package.ps1` fails if they are missing. The `package` job in `build.yml` runs it on every push so this cannot silently regress.

## Other automation
- `build.yml`: build + test on every push to `master` and every pull request, plus a packaging smoke test.
- `.github/dependabot.yml`: weekly NuGet update PRs (grouped), monthly GitHub Actions updates.
- aria2 itself is pinned (version and SHA-256) in `tools/get-aria2.ps1`; update it by hand so the checksum stays a real check.
