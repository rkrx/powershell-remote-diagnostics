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

Build outputs, test results and local settings are ignored by Git. Source paths in project-owned debug symbols are mapped to a generic /_/ prefix so distributed binaries do not embed a developer's checkout path. Do not add generated files or machine-specific configuration to the repository.

Build the portable Windows distribution with scripts/Publish-Windows.ps1. The resulting artifacts directory is for local distribution output and is not committed.
