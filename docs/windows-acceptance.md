# Server and client acceptance

Test the server on Windows 10 Pro 22H2 x64, Windows 11 x64 and Windows Server 2016/2022/2025 x64. Record OS build, patch level and package revision privately. Use a clean machine without separate .NET/PowerShell installations; see platform qualification.

- [ ] Extract the entire server ZIP, including support/. `powershell-rc.exe --help` and `--version` work.
- [ ] Start on loopback: production/manual/read-only defaults and one secret connection string are shown. No standalone token or plaintext REST endpoint is advertised.
- [ ] Connect using the separate MCP client and full connection URL, without any GUI opening.
- [ ] A missing URL opens Avalonia on an interactive client desktop. Cancel cancels connection; malformed input produces a generic validation message. No secret appears in process arguments or stdout logs.
- [ ] A modified token or fingerprint is rejected; an expired certificate is rejected even with its matching pin. No insecure fallback occurs.
- [ ] Verify TLS/HTTP2 from Windows and macOS clients, especially against Server 2016 and Windows 10 Pro 22H2.
- [ ] Read server_info and confirm actual runtime information and declaration.
- [ ] Submit `'ready'; Get-Date; Get-Item -LiteralPath $PSHOME`; confirm the package's own modules load without installed PS7.
- [ ] Approval question light purple; approval green; denial/cancellation red; request purple; reason cyan; script yellow; captured output neutral. Verify narrow windows, multiline arrays/pipelines, introductory comments and no source truncation.
- [ ] Leave approval pending over 15 seconds; execute returns pending with UUID. Poll, approve and obtain final output under the same UUID.
- [ ] Verify variables/functions/location persist between scripts; concurrent submissions execute FIFO with one approval each.
- [ ] Verify denial, queued/running cancellation, native exit codes and native processes receiving EOF on stdin.
- [ ] Retrieve more than 200 output lines, a line longer than 4096 codepoints, Unicode offsets, search context and cursor pages. Verify retention evicts the oldest of 21 completed results.
- [ ] Disconnect a client while work is running: no duplicate submission or implicit cancellation. Restart server: old URL fails, new URL succeeds, session/results reset.
- [ ] Verify the Windows client package runs without PowerShell or a separate .NET installation. Verify MCP stdout contains protocol only.

Interactive server applications, credential/UAC prompts and rollback of executed scripts are not provided. Forced termination can leave temporary output files. Automated CI on Server 2022 and local macOS checks do not complete acceptance on other target OS versions. No new macOS archives are published; historic archives are not Developer ID signed/notarized.

## Relay and exclusive sessions (0.3)

- [ ] Both archives contain only their executable and support/ at root; launch from a different working directory without installed .NET/PowerShell.
- [ ] Start `powershell-rc.exe relay` without PowerShell initialization and connect an RC outbound using the registration URL.
- [ ] Use the RC client URL through the relay; verify certificate pin and token failures are rejected.
- [ ] First client connects, second receives AlreadyExists, and explicit disconnect permits the next client without changing the public port.
- [ ] Accepted work and results survive client disconnects; no automatic retries or cancellation occur.
- [ ] Stop/crash RC and confirm public listener closes and port is reusable after graceful cleanup/heartbeat expiry.
- [ ] Connection logs show only nonsecret session ID/mode, never bearer/lease credentials.
- [ ] Optional Windows Avalonia entry dialog still spawns from the packaged client and returns through its private pipe.
