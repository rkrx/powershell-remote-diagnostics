# Server and client acceptance

Test the server on Windows 10 Pro 22H2 x64, Windows 11 x64 and Windows Server 2016/2022/2025 x64. Record OS build, patch level and package revision privately. Use a clean machine without separate .NET/PowerShell installations; see platform qualification.

- [ ] Extract the entire server ZIP. `powershell-rc.exe --help` and `--version` work.
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
- [ ] Verify the Windows and macOS client packages run without PowerShell or a separate .NET installation. Verify MCP stdout contains protocol only.

Interactive server applications, credential/UAC prompts and rollback of executed scripts are not provided. Forced termination can leave temporary output files. Automated CI on Server 2022 and local macOS checks do not complete acceptance on other target OS versions. macOS client archives are not Developer ID signed/notarized.
