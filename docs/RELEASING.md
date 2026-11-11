# Releasing

Releases are started by hand. When you decide master is good enough, you run the **release** workflow on GitHub and give it a version. You stay in control of the number and the timing; nothing releases on its own.

## Versioning
- [SemVer](https://semver.org): `MAJOR.MINOR.PATCH`, optional pre-release suffix (`-beta.1`, `-rc.1`).
- The one place the number lives is `<Version>` in `Directory.Build.props`. The build stamps it into the exe (file properties, About page, log line "Aria2NET x.y.z starting").
- The git tag is that number with a `v` in front: `v0.4.0`, `v1.0.0-rc.1`. The release workflow creates it; `Directory.Build.props` keeps only `MAJOR.MINOR.PATCH`.
- Below 1.0 every release is marked **pre-release** on GitHub (anything can still change). From 1.0 on only versions with a suffix are pre-releases.
- Roadmap versions map to releases: roadmap 0.5 -> `0.5.0`; fixes after that -> `0.5.1`.

## Cutting a release
1. Make sure `master` is green on GitHub (the `build` workflow). Install the Setup.exe (or run the portable zip) from a dry run before the real release, on a hybrid-GPU laptop with an external monitor if you can: the app ships its own WinUI runtime, and a runtime bug (2.6.4) once garbled all text in a release while every automated check passed.
2. GitHub -> **Actions** -> **release** -> **Run workflow** on `master`. Enter the version without `v` (`0.4.0`). Tick **dry run** to only build and package: the installer, portable zip and update packages are attached to the run and nothing is committed, tagged or published.
3. The workflow then:
   - **verify**: the version must look like `X.Y.Z[-suffix]`, not be lower than the current `<Version>` and not already have a tag. It writes the version into `Directory.Build.props` on the runner, then builds and tests.
   - **package**: `tools/package.ps1` for `win-x64` and `win-arm64`. It publishes a self-contained build (no .NET or Windows App SDK install needed, `aria2c.exe` bundled), downloads the previous release so Velopack can build delta packages, and runs `vpk pack`. Each processor gets its own Velopack channel.
   - **release**: only now does anything reach the repo. It commits the version bump to `master` (if the number changed; message `Release v0.4.0`), pushes that commit and the tag together, then `vpk upload github` creates the GitHub Release (published, pre-release below 1.0) with both processors' packages, and GitHub's generated notes are added to it.
4. Pull `master` to get the bump. Edit the generated release notes if you want (they come from commit/PR titles since the previous tag).

If any build, test or package step fails, nothing is pushed. If `master` moved while the workflow ran, the push is refused (the branch and tag are pushed atomically) and you just run it again. If a release was created by mistake, delete the release and the tag (`git push origin :refs/tags/v0.4.0`); the bump commit can stay.

The workflow pushes to `master` with the built-in token, so it fails if branch protection requires PRs for `master`. In that case allow GitHub Actions to bypass the rule, or tell me and the bump can move into a PR.
## Building a package locally
```
pwsh -ExecutionPolicy Bypass -File tools/package.ps1 -Rid win-x64 -Version 0.8.0
```
Output goes to `artifacts/<rid>/` (git-ignored). `win-arm64` builds on an x64 machine too, but cannot be run there. `vpk` is a pinned local tool (`.config/dotnet-tools.json`); keep its version equal to the `Velopack` package in `Aria2NET.csproj`.

## What a release contains
Per processor (`win-x64`, `win-arm64`):
- `SECT19N.Aria2NET-<rid>-Setup.exe`: the installer. Per user, no admin rights, installs to `%LocalAppData%\SECT19N.Aria2NET` and adds Start menu and desktop shortcuts. Installed copies update themselves from the release feed.
- `SECT19N.Aria2NET-<rid>-Portable.zip`: unzip and run. Never updates itself; download the next one from the Releases page.
- `SECT19N.Aria2NET-<version>-<rid>-full.nupkg`, delta packages and `releases.<rid>.json` / `RELEASES-<rid>`: the update feed. Not meant for people.

Known limits:
- No certificate is needed to install (this is not MSIX), but the exes are **not code signed**, so Windows SmartScreen warns on first run ("More info" -> "Run anyway"). Free signing through the SignPath Foundation OSS program and a winget manifest are planned for 0.9 (see `ROADMAP.md`).
- The package id `SECT19N.Aria2NET` is permanent: installed copies look for updates under it. It also must stay different from the data folder name `Aria2NET`.
- aria2 has no official arm64 build, so the arm64 package ships the **x64** `aria2c.exe` (runs under emulation).
- `dotnet publish` skips the compiled XAML and the app's resource index for unpackaged WinUI apps; `Aria2NET.csproj` has a target that adds them and `package.ps1` fails if they are missing. The `package` job in `build.yml` runs it, but only on a manual run (after build + test pass), so run that workflow by hand before merging anything that touches packaging.
- Copies older than 0.8 (zip installs) cannot update themselves. Install 0.8 once with Setup.exe. A newer copy asks an older running copy to exit instead of being handed over to it.
## Other automation
- `build.yml`: build + test on every pull request (drafts skipped, superseded runs cancelled). "Run workflow" on any branch also runs a packaging smoke test after build + test pass. Nothing runs on pushes to `master`.
- `.github/dependabot.yml`: one grouped NuGet PR and one grouped Actions PR per month, at most one open of each. Dependabot PRs are not built automatically; to check one, Actions -> build -> Run workflow on its branch.
- aria2 itself is pinned (version and SHA-256) in `tools/get-aria2.ps1`; update it by hand so the checksum stays a real check.
