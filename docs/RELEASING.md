# Releasing

Releases are started by hand. When you decide master is good enough, you run the **release** workflow on GitHub and give it a version. You stay in control of the number and the timing; nothing releases on its own.

## Versioning
- [SemVer](https://semver.org): `MAJOR.MINOR.PATCH`, optional pre-release suffix (`-beta.1`, `-rc.1`).
- The one place the number lives is `<Version>` in `Directory.Build.props`. The build stamps it into the exe (file properties, About page, log line "Aria2NET x.y.z starting").
- The git tag is that number with a `v` in front: `v0.4.0`, `v1.0.0-rc.1`. The release workflow creates it; `Directory.Build.props` keeps only `MAJOR.MINOR.PATCH`.
- Below 1.0 every release is marked **pre-release** on GitHub (anything can still change). From 1.0 on only versions with a suffix are pre-releases.
- Roadmap versions map to releases: roadmap 0.4 -> `0.4.0`; fixes after that -> `0.4.1`.

## Cutting a release
1. Make sure `master` is green on GitHub (the `build` workflow).
2. GitHub -> **Actions** -> **release** -> **Run workflow** on `master`. Enter the version without `v` (`0.4.0`). Tick **dry run** to only build and package: the zips are attached to the run and nothing is committed, tagged or published.
3. The workflow then:
   - **verify**: the version must look like `X.Y.Z[-suffix]`, not be lower than the current `<Version>` and not already have a tag. It writes the version into `Directory.Build.props` on the runner, then builds and tests.
   - **package**: `tools/package.ps1` for `win-x64` and `win-arm64`. Each zip is self-contained (no .NET or Windows App SDK install needed) and bundles `aria2c.exe`.
   - **release**: only now does anything reach the repo. It commits the version bump to `master` (if the number changed; message `Release v0.4.0`), pushes that commit and the tag together, and creates the GitHub Release with generated notes, both zips and a `.sha256` file for each.
4. Pull `master` to get the bump. Edit the generated release notes if you want (they come from commit/PR titles since the previous tag).

If any build, test or package step fails, nothing is pushed. If `master` moved while the workflow ran, the push is refused (the branch and tag are pushed atomically) and you just run it again. If a release was created by mistake, delete the release and the tag (`git push origin :refs/tags/v0.4.0`); the bump commit can stay.

The workflow pushes to `master` with the built-in token, so it fails if branch protection requires PRs for `master`. In that case allow GitHub Actions to bypass the rule, or tell me and the bump can move into a PR.
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
