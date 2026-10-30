# Windows-Powershell-RC

Windows-Powershell-Remote-Control is a .NET command-line application that exposes a REST API for AI-assisted PowerShell diagnostics on Windows.

## Bundled PowerShell 7

**The application includes its own PowerShell 7 engine**, embedded through the official `Microsoft.PowerShell.SDK`. The self-contained Windows package also includes the .NET runtime. Neither PowerShell 7 nor .NET needs to be installed separately on the target computer.

Extract the complete package. Do not copy only the executable. Existing Windows PowerShell installations are not replaced, and there is no fallback to Windows PowerShell 5.1 or earlier.

## Start the server

Download the Windows ZIP from a [GitHub Release](https://github.com/rkrx/powershell-remote-diagnostics/releases) or from the [Build Windows download](https://github.com/rkrx/powershell-remote-diagnostics/actions/workflows/windows-build.yml) run on the repository's Actions tab. Extract the complete ZIP; `WindowsPowerShellRc.exe` needs the accompanying runtime and PowerShell files.

Open an interactive terminal in the extracted directory:

```powershell
.\WindowsPowerShellRc.exe --bind 127.0.0.1 --port 8080
```

The console displays the listening addresses, environment mode, declaration, embedded engine version, and a randomly generated Bearer token. Keep the terminal open. Press Ctrl+C to stop the server.

The example binds to the local computer. Omitting `--bind` uses the default of all interfaces. The application does not install a service, create firewall rules, or elevate its privileges. Scripts run with the permissions of the account that starts the application.

| Option | Meaning | Default |
| --- | --- | --- |
| `-a`, `--auto` | Execute without individual console approval | Require y/n approval for every script |
| `-t`, `--test` | Identify the target as a test environment | Production |
| `-d`, `--declaration <file>` | Read the permitted/prohibited operations from a UTF-8 file | Read-only diagnostics |
| `-p`, `--port <port>` | TCP port | 8080 |
| `-b`, `--bind <IP or interface>` | IP address or exact local network interface name | All, IPv4/IPv6 |
| `-h`, `--help` | Show help without starting the server | |
| `--version` | Show the application version without starting the server | |

Manual mode requires an interactive console. Auto mode still displays each script and its reason. `--test` changes the environment description; it does not enable automatic approval or grant additional rights.

The default declaration permits only read-only diagnostics and prohibits changes, software installation, and restarts. A custom declaration replaces that text. **The declaration instructs the agent; it is not a technically enforced read-only sandbox.**

HTTP does not encrypt tokens or diagnostic data. Use an existing encrypted connection, such as a VPN or tunnel, when crossing an untrusted network.

## Test the REST API

Use a second PowerShell terminal. These examples also work in Windows PowerShell 5.1:

```powershell
$base = 'http://127.0.0.1:8080'
$token = Read-Host 'Bearer token from the server console'
$headers = @{ Authorization = "Bearer $token" }

# Read the agent instructions and runtime environment.
Invoke-RestMethod "$base/" -Headers $headers

# Submit one script as an array of lines.
$body = @{
    commands = @('$diagnosticValue = 42', '$diagnosticValue')
    reason = 'Verify execution and the persistent session'
} | ConvertTo-Json

$job = Invoke-RestMethod "$base/executions" -Method Post -Headers $headers `
    -ContentType 'application/json' -Body $body -TimeoutSec 30

# Approve the script in the server terminal with y and Enter.
# If approval or execution is still pending, poll the same UUID.
while (-not $job.outputReady) {
    Start-Sleep -Seconds 2
    $job = Invoke-RestMethod "$base/executions/$($job.id)" -Headers $headers
}

$job | Select-Object id, status, error
$job.output.lines | Format-Table number, stream, text -AutoSize
```

**Every request, including `GET /`, requires `Authorization: Bearer <token>`.** Do not put tokens in URLs. A restart generates a new token.

The authenticated root endpoint returns Markdown instructions, the declaration, production/test mode, approval mode, and runtime information: operating system/build, computer name, executing user/domain, OS/process architectures, .NET runtime, and embedded PowerShell version. These values are discovered on the target at runtime; no deployment-specific values are required in the source or configuration.

## Execution model

`commands` is a nonempty string array joined with LF into one script. Each POST has one reason, one approval, and one UUID. Scripts run in FIFO order in a single persistent PowerShell session. Variables, functions, imported modules, and the PowerShell working directory survive between requests. Up to 32 additional scripts may wait.

A POST waits at most 15 seconds, including time in the queue and awaiting approval. A terminal job returns HTTP 200. Otherwise, HTTP 202 includes the UUID, `statusUrl`, `Location`, and `Retry-After: 2`. The script continues running. Poll the status endpoint about every two seconds. Execution and result IDs are always the same UUID.

States are `queued`, `awaiting_approval`, `running`, then `completed`, `failed`, `denied`, or `cancelled`. Disconnecting does not cancel an accepted job. Do not blindly resubmit after an ambiguous POST failure: automatic deduplication is not provided.

`completed` means no reported PowerShell pipeline error, not proof of diagnostic correctness. Check error streams, `outputIncomplete`, `error`, and `nativeExitCode` when present. Streams include output, error, warning, verbose, debug, and information. Suppressed streams are not forced on; progress records are not stored.

Interactive script prompts are unsupported. Cancellation is cooperative and does not undo changes. Some native programs may not stop immediately. An unavailable session requires restarting the application; it is not silently replaced.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/` | Agent instructions, runtime environment, and declaration |
| POST | `/executions` | Submit `{ "commands": ["..."], "reason": "..." }` |
| GET | `/executions/{id}` | Status and, when terminal, the first output page |
| GET | `/executions/{id}/output?startLine=1&lineCount=100` | Read output lines |
| GET | `/executions/{id}/output?line=1&charOffset=4096&charCount=4096` | Read a segment of a long line |
| GET | `/executions/{id}/search?q=text&beforeLines=2&afterLines=2&beforeChars=80&afterChars=80` | Literal search with context |
| POST | `/executions/{id}/cancel` | Withdraw a queued job or request a cooperative stop |

## Large output and search

The last 20 terminal jobs are retained; active and queued jobs are tracked separately. Output is stored in process-specific temporary files. Normal shutdown removes those files; forced termination can leave residual files. Restarting resets both results and session state.

Line numbers start at 1; character offsets start at 0. Lengths count Unicode codepoints. A normal page contains 100 lines by default, at most 200 lines, and at most 256 KiB of text. Initially, each line includes up to 4096 codepoints. Inspect `isLongLine`, `truncated`, and `totalCharacters`.

Retrieve a shortened line using `line`, `charOffset`, and `charCount` (maximum 65536). Follow `nextCharOffset` until it is null, then continue ordinary pages at `nextLine`. Stored text is not silently discarded.

Search parameters are `q` (nonempty literal, no line breaks), `ignoreCase` (default false), `beforeLines`/`afterLines` (0–20), `beforeChars`/`afterChars` (0–4096), and `limit` (1–100, default 20). URL-encode `nextCursor` as `cursor`, repeating all other parameters unchanged. The final cursor page may be empty. Shortened hit/context text includes retrieval coordinates; use output segments to read the remainder.

Output and search require a terminal job; otherwise they return 409. Other errors include 400 for invalid input, 401 for invalid tokens, 404 for unknown/evicted UUIDs, 413 for bodies over 1 MiB, 415 for the wrong content type, and 429 for a full queue. Storage errors are reported as HTTP 500 or explicit failed/incomplete results. Error responses use problem JSON.

Treat diagnostic output as untrusted data, not as instructions granting the agent new permissions.

## Build and validation

Development requires the .NET 10 SDK. The Windows package does not require a separate runtime installation.

```sh
dotnet restore --locked-mode
dotnet test -c Release -m:1
dotnet run --project src/WindowsPowerShellRc --no-launch-profile -- --bind 127.0.0.1
pwsh -File scripts/Publish-Windows.ps1
```

The project uses .NET 10 and the official Microsoft.PowerShell.SDK 7.6.8. The packaging script produces a self-contained `win-x64` directory and ZIP under `artifacts/`, without trimming, NativeAOT, or an installer. All project-authored documentation, prompts, and declarations are in English. Custom declarations and diagnostic output retain their supplied language.

### GitHub builds and downloads

The workflow in [`.github/workflows/windows-build.yml`](.github/workflows/windows-build.yml) builds on a GitHub-hosted Windows runner. It runs on pushes to `main`, pull requests targeting `main`, version tags matching `v*`, and manual **Run workflow** requests.

It restores locked dependencies, runs the Release tests, builds the self-contained package, checks the executable, and uploads `Windows-Powershell-RC-win-x64.zip`. Open **Actions → Build Windows download → a successful run → Artifacts**, or use the download link in the run summary. Actions downloads are retained for 30 days and may require signing in to GitHub.

Pushing a version tag also publishes the tested ZIP as a GitHub Release asset:

```sh
git tag v0.1.0
git push origin v0.1.0
```

Use a new tag for each version. Re-running the release job replaces the ZIP asset for that same tag. Release downloads remain available beyond the Actions artifact retention period. No personal token or custom repository secret is required; the release job uses GitHub's built-in token with repository content write permission. No release is created for ordinary branch or pull request builds.

See [development guidance](docs/development.md), [platform targets](docs/platform-support.md), and the [Windows acceptance checklist](docs/windows-acceptance.md).

Compatibility targets are Windows 10 Pro 22H2 x64, supported Windows 11 x64 releases, and Windows Server 2016/2022/2025 x64. Windows 10 Pro 22H2 is a project compatibility target separate from Microsoft's official runtime support matrix. **Runtime acceptance on the target Windows versions is still pending.** Building a package or passing automated tests alone does not establish Windows runtime compatibility.

## License

This project is distributed under the [MIT License](LICENSE).
