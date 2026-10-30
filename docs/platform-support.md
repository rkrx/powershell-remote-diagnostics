# Windows platform targets

Last checked: 2026-10-30. Distribution: `win-x64`, bundled .NET 10 and PowerShell SDK 7.6.8. No separate .NET/PowerShell installation and no legacy-engine fallback.

| Target | Dependency compatibility | Project validation |
| --- | --- | --- |
| Windows Server 2016 x64 | Listed in the .NET 10 supported OS matrix | Windows acceptance pending |
| Windows Server 2022/2025 x64 | Listed in the .NET 10 supported OS matrix | Windows acceptance pending |
| Windows 11 x64 | Supported releases are listed in the .NET 10 matrix | Windows acceptance pending |
| Windows 10 Pro 22H2 x64 | Explicit project compatibility target; not listed in the current official .NET 10 support matrix | Windows acceptance pending |

The existing runtime and native stdin APIs do not require a separate Server-2016 package or a runtime downgrade. This is a compatibility conclusion based on the dependencies and the APIs used, not evidence that the program has run on Server 2016.

## Windows 10 qualification

Microsoft's current .NET 10 matrix lists Windows 10 21H2 Enterprise/IoT LTSC, 1809 Enterprise LTSC and 1607 Enterprise LTSB. It does not give blanket support to every Windows 10 edition/build. In particular, ordinary 22H2 Home/Pro/Enterprise must not be labelled officially supported merely because the binary may run there; ESU and the OS/runtime support policies need to be considered separately for the actual deployment.

**Windows 10 Pro 22H2 x64** is an explicit project compatibility target. It uses the same self-contained `win-x64` package with bundled .NET 10 and PowerShell 7; no runtime downgrade, separate installation or artificial OS-version block is introduced.

Project compatibility is a separate claim from Microsoft's official runtime/OS support. Admission to the project target list does not mean execution has been validated. Actual acceptance must record the Windows 10 Pro 22H2 edition/build, patch level and ESU status, and run the complete [Windows acceptance checklist](windows-acceptance.md). Runtime compatibility must be verified on this exact target edition.

## Agent discovery

Authenticated `GET /` reports production/test mode, approval mode, embedded PowerShell version, OS description and version/build, computer name, executing user (including domain on Windows), OS architecture, process architecture and .NET runtime. OS description comes from the .NET runtime; the numeric OS version identifies the kernel/build and is not a substitute for checking a Windows product's edition or support lifecycle.

## Sources

- [Official .NET 10 supported OS matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [Microsoft: .NET installation and Windows support](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [Microsoft: PowerShell support lifecycle and supported platform criteria](https://learn.microsoft.com/en-us/powershell/scripting/install/powershell-support-lifecycle)
