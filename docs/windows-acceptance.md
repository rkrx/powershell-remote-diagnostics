# Windows acceptance — pending

Perform on Windows 10 Pro 22H2 x64, Windows 11 x64 and Windows Server 2016/2022/2025 x64; see [platform qualification](platform-support.md). Record OS edition/build, patch level, ESU/LTSC status where relevant, package commit, date and outcome. Ideally use a clean VM with neither a separately installed .NET runtime nor PowerShell 7. Do not install prerequisites to make the test pass without recording that as a failure of the portable package requirement.

- [ ] Run the complete checklist on Windows 10 Pro 22H2 x64 with the existing self-contained win-x64 package. Record exact OS build/patch level and ESU status; do not infer success from Windows 11 or Server tests.

- [ ] Unzip the complete package; `WindowsPowerShellRc.exe --help` and `--version` work without installation.
- [ ] Start bound to `127.0.0.1`; startup reports embedded PowerShell 7.6.8, production/manual defaults, declaration and a random token.
- [ ] GET / and all execution/output/search/cancel paths reject missing/wrong tokens with 401.
- [ ] Authenticated GET / reports the actual OS/build, computer name, user/domain, OS/process architectures and .NET runtime, as well as production/test and PowerShell version.
- [ ] Submit `$rcValue = 42` with a reason. Full script/reason appear; n denies, y executes. A later `$rcValue` returns 42.
- [ ] Submit two jobs simultaneously. The second waits; each has its own UUID and one approval.
- [ ] Leave approval pending for over 15 seconds: HTTP returns 202 with UUID. Poll, then approve and obtain final output under the same UUID.
- [ ] Run `Start-Sleep -Seconds 17; 'done'`: POST returns after approximately 15 seconds, polling later returns the result.
- [ ] Cancel during approval, then type y: the cancelled script never executes and the next request has its own prompt.
- [ ] Cancel an executing sleep; a later script still uses the same session. No new session is silently created.
- [ ] `Write-Error`, `Write-Warning`, `Write-Host` and normal output retain stream labels; nonterminating error yields failed while following output remains accessible.
- [ ] `Read-Host`/credential/choice prompts fail clearly. A benign native program captures stdout/stderr and sees EOF on stdin without consuming approval input. Native cancellation is checked separately.
- [ ] Generate a line with over 4096 Unicode codepoints and reconstruct it fully via character ranges; search with line and character context and cursor pagination.
- [ ] Complete 21 jobs; the oldest becomes 404 while the latest 20 remain readable.
- [ ] Custom declaration and --test appear on GET /; --test does not enable auto or grant rights.
- [ ] Bind All accepts IPv4 and IPv6 where available; explicit address/interface and invalid-interface errors work.
- [ ] Ctrl+C stops acceptance, attempts pipeline cancellation and cleans up this process's temporary files. No firewall/service installation occurs.

Hard termination can leave process-specific temporary files. Cooperative cancellation cannot guarantee immediate termination of every native program and never rolls back changes. Do not treat successful macOS tests or a ZIP build as completion of this checklist.
