# Development and validation

Use .NET 10 and the committed package lockfiles:

```sh
dotnet restore --locked-mode
dotnet test -c Release -m:1 --no-restore
```

## Components

- `src/WindowsPowerShellRc`: Windows server, embedded PowerShell, approval queue, output store and authenticated gRPC adapter.
- `src/WindowsPowerShellRc.Transport`: PowerShell-free protobuf contracts, strict connection-string parsing and pinned TLS channel creation.
- `src/WindowsPowerShellRc.Client`: local MCP stdio host, retained connections and optional Avalonia input subprocess.

Server startup generates a process-specific certificate and access key. On Windows its private key uses a temporary OS-managed user container for Schannel compatibility (no PersistKeySet); normal certificate disposal removes the key. The versioned opaque connection token contains the key and certificate fingerprint. Pinning replaces CA/hostname trust with the exact copied certificate identity and enforces certificate validity. Do not add a blanket certificate-validation callback, plaintext fallback or automatic execution retry.

MCP stdout must contain only protocol messages. Do not register ordinary console log providers or log tool arguments. GUI entry uses an inherited anonymous pipe; secrets are not process arguments, environment variables or temporary files. A supplied complete URL bypasses the prompt. Failed supplied URLs do not open GUI automatically.

## Tests

The suite covers TLS pin/auth rejection, token parsing, gRPC queue/polling/errors, direct connection versus prompt behavior, MCP initialize/list/call over real stdio, actual server PowerShell execution, native stdin isolation, approvals/cancellation, retention, Unicode paging/search, storage faults and readable previews.

`PackagedHostTests` and `McpProcessTests` can run against actual extracted packages by setting `RC_TEST_SERVER_EXECUTABLE` and `RC_TEST_CLIENT_EXECUTABLE` to the corresponding absolute executable paths. These variables contain no credentials. Subprocess tests start servers bound to loopback with synthetic scripts. The client subprocess must use its own complete output directory; a project-reference copy under the test host can omit dependencies supplied by the test host's shared ASP.NET runtime.

Verify the optional dialog on an interactive desktop: missing URL opens it, Cancel returns a cancelled connection, malformed input stays in the dialog with a generic validation error, and a pasted working URL connects. Direct-URL integration tests must work without a desktop.

## Publishing

`Publish-Windows.ps1` builds separate self-contained server/client ZIPs. `Publish-Package.ps1 -Component Client -Runtime osx-arm64` builds the Apple Silicon client archive. The server includes its own PowerShell 7 engine and stages SDK built-in module manifests under `$PSHOME/Modules`; the client must not contain `System.Management.Automation.dll`. Publishing rejects project binaries/symbols containing the checkout path.

RID-specific publishing relocates `System.Management.Automation.dll` to the application root. Its module manifests must accompany it under `Modules`; otherwise a development build can work while published requests fail at `Out-String`. Startup tests that formatter before accepting gRPC traffic.

Build outputs and private verification records are not committed. Project source/debug metadata maps checkout paths to `/_/`, including SourceLink metadata. Keep real hostnames, accounts, tokens, declarations and diagnostic output out of Git.

GitHub runs the full suite and extracted Windows ZIP gRPC/MCP tests before upload. A separate macOS job builds and checks MCP startup from the client package. Release assets are attached only after required build jobs succeed. A manual release-tag backfill builds the tagged source, not current main. Actual older-Windows TLS/HTTP2 qualification and macOS signing/notarization remain separate deployment checks.
