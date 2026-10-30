# Development and validation

## Toolchain

Use the .NET 10 SDK and restore the committed package lockfiles. The supported distribution target is win-x64; framework-dependent development builds can run on compatible SDK host platforms.

```sh
dotnet restore --locked-mode
dotnet test -c Release -m:1
dotnet build -c Release --no-restore -m:1
```

Run the server bound to loopback for local testing. Manual mode needs an interactive console. Use --auto only when unattended execution is intentional.

## Validation scope

The automated suite covers CLI validation, authentication, persistent PowerShell state, streams, FIFO execution, approval/cancellation, HTTP polling, output retention, Unicode pagination, contextual search, storage failures, and native stdin isolation.

Use the Windows acceptance checklist for the actual target OS versions. Record the OS edition/build, patch level, package revision and outcome in a private test record. Do not commit real hostnames, accounts, tokens, diagnostic output, local paths, or deployment declarations.

## Build artifacts

Build outputs, test results and local settings are ignored by Git. Source paths in project-owned debug symbols are mapped to a generic /_/ prefix. Continuous-integration path normalization also applies to SourceLink metadata; the packaging script rejects project binaries or symbols containing the checkout path. Do not add generated files or machine-specific configuration to the repository.

Build the portable Windows distribution with scripts/Publish-Windows.ps1. The resulting artifacts directory is for local distribution output and is not committed.

## GitHub Actions

The Windows download workflow runs the Release suite on `windows-2022` before using the existing packaging script. Successful runs expose the complete self-contained ZIP as an Actions artifact, retained for 30 days. Publishing a GitHub Release (including a prerelease) builds its tagged revision and attaches the tested ZIP as a release asset, regardless of whether the tag has a `v` prefix. Draft releases do not trigger a build. Version tag pushes matching `v*` also create a release with the package.

A manual run is available from the Actions tab. The optional `release_tag` input checks out exactly `refs/tags/<release_tag>` and publishes the package for that release; an empty input produces only the ordinary build artifact. Select `main` for the workflow definition when backfilling an older release. Release/tag builds use tag-specific concurrency groups and are not cancelled by newer branch builds. Ordinary builds have read-only repository permissions.

The release job has content write permission and uses the built-in GitHub token. No deployment host, private path, custom secret, or external infrastructure is configured. Windows runner validation does not replace acceptance on every OS edition listed in the platform targets.
