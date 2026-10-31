# Reverse relay, exclusive sessions, and Windows distribution

Status: reviewed draft for approval. Independent builder check found no important planning gaps. Supersedes the client-disconnect port-release requirement: the owner explicitly decided that a port belongs to an RC until that RC's relay connection ends.

## Intent and constraints

Allow a Windows RC behind inbound firewall/NAT restrictions to be reached through an operator-controlled relay. Keep diagnostics encrypted end to end and preserve approval, persistent PowerShell state, UUID executions, FIFO ordering, pagination, search, and cancellation. Only one authenticated diagnostic client may own a given RC at a time, whether connected directly or through the relay.

Ship Windows x64 client and server downloads only in GitHub Actions. Keep macOS source development available without macOS CI jobs or new macOS release assets. Linux remains out of scope. The server package contains both normal RC mode and relay mode; the server executable is named `powershell-rc.exe`. Keep the client executable name unchanged.

Project docs, CLI output, and defaults remain English and generic. No real credentials, hostnames, or local environment details go into the repository.

## Approaches and selected choice

1. TLS passthrough reverse tunnel: relay forwards raw TCP bytes, preserving the existing RC certificate pin and bearer authentication. Requires relay registration, allocation, and tunnel transport, but no second implementation of diagnostics. Selected by the owner.
2. Terminating gRPC proxy: relay authenticates the diagnostic client and reissues diagnostic calls to the RC. Allows future inspection but introduces another trust boundary and duplicated diagnostic routing. Not selected.

An external reverse-tunnel product would add another installation and configuration surface and would not provide the requested integrated executable mode. It is not used. Existing .NET/gRPC transport libraries will carry the reverse connection; no third-party tunnel service is introduced.

## CLI and credentials

Proposed interface (builder choice):

```powershell
.\powershell-rc.exe relay --port 8080 --bind All --advertise-host relay.example --port-range-start 20000 --port-range-end 25000
.\powershell-rc.exe --relay 'grpc://<relay-registration-token-and-pin>@relay.example:8080'
```

Normal direct RC flags remain supported. In relay mode the RC accepts diagnostics on an internal loopback-only ephemeral port; it does not expose its normal public diagnostic listener. Direct and relay operation simultaneously are out of scope. Existing manual/auto/test/declaration behavior is retained. Relay mode must not initialize the PowerShell engine or require a script-approval console. Range endpoints are inclusive; reject invalid ranges and collisions with the control port before listening. Defaults: control port 8080, range 20000–25000, bind All. `--advertise-host` is optional and defaults to the machine hostname; require a concrete host, never an unspecified bind address.

The relay generates a fresh certificate and random registration secret at startup and prints a pinned registration connection string. The operator supplies this to the RC through `--relay`. The relay registration credential is distinct from every RC's diagnostic credential. It authorizes allocating relay capacity, never PowerShell access. The relay receives no RC bearer secret or RC private key. Command-line secret visibility to the local OS/operator is documented; no claim of process-list secrecy is made.

The authenticated registration returns the allocated public host and port. The RC prints its ordinary diagnostic URL, using its own existing bearer secret and certificate fingerprint with the returned relay host/port. The client uses this URL through its existing MCP connect tool or optional dialog; no relay-specific GUI is needed. The relay URL is an explicitly disclosed endpoint from a trusted relay; no automatic arbitrary redirects are followed.

## Relay transport and ownership

Use a separate versioned relay protobuf service over TLS on the fixed control port, sharing existing gRPC and pinning primitives. A long-lived bidirectional registration stream owns one allocation and delivers incoming connection requests. Each incoming external TCP socket is paired with a separate RC-initiated bidirectional gRPC data stream. The RC bridges that stream to its loopback diagnostic listener. Forward TLS bytes without parsing or terminating the inner TLS connection. There are two TLS layers on the RC-to-relay leg: outer relay transport and inner diagnostic TLS.

Bind the actual public socket before announcing its allocation. Skip occupied ports; fail registration clearly when no port is available. Never select a port by checking availability and then closing the checking socket. Allocation changes are atomic across registrations. Each registration gets an unpredictable internal lease credential; each incoming socket gets a one-use connection identifier. Data streams must authenticate both the relay registration secret and the owning lease credential; another registered RC cannot claim another RC's sockets. These internal credentials never appear in logs or agent URLs.

The public listener belongs to the RC registration, not to an external socket or diagnostic client session. A client disconnect closes that socket and its data stream only. Port and advertised URL stay unchanged while the RC remains registered. Stopping the RC cancels registration and all tunnels and closes the public listener before returning the port to the allocator. Relay shutdown performs the same cleanup for every registration. The port becomes available only after its old listener and tunnels have been closed; no traffic may cross from an old allocation into a reused port.

Builder defaults: at most 100 RC registrations, at most 8 accepted/pending TCP tunnels per registration, 32 KiB data frames, bounded per-tunnel buffering, a 10-second tunnel-establishment deadline, and backpressure rather than unbounded message queues. These are implementation limits, not new CLI switches for the MVP. Malformed or oversized frames terminate the offending stream; limits and timeout paths release all sockets/tasks. Unknown/duplicate tunnel claims are rejected.

Registration has an application heartbeat at 5-second intervals and expires after 20 seconds without valid liveness traffic. This bounds port retention after a crashed RC or network partition. A graceful RC exit releases immediately. A transient relay/control failure ends the allocation; the RC reports the loss and exits nonzero rather than silently re-registering or changing URLs. The operator restarts it to reconnect. Automatic registration recovery with a stable URL is out of scope.

## Exclusive diagnostic client sessions

A gRPC channel alone is not a durable authenticated session. Introduce an authenticated long-lived bidirectional diagnostic session stream. After verifying the bearer credential, the server atomically admits one stream and returns an unpredictable per-session credential. A second authenticated session attempt fails with `AlreadyExists` and a sanitized busy explanation. Invalid bearer credentials are rejected before admission and cannot reserve a slot. Every unary diagnostic call, including GetInfo, must carry both the RC bearer and active session credential. Update the client and server together; old 0.2 clients fail with a clear session-required status. Document the protocol compatibility change.

The client opens the session and receives its acknowledgement before calling GetInfo or retaining a connection ID. It sends heartbeats every 5 seconds. The server releases ownership on explicit stream completion/cancellation, or after 20 seconds without a valid heartbeat. A dead client therefore delays a replacement client by at most the detection period under normal scheduling. Heartbeat expiry is independent of script duration and script approval. The implementation checks both owner identity and generation on cleanup so a late callback cannot release a newer session.

If session establishment or subsequent GetInfo fails, dispose the session stream and channel and release ownership. Client disconnect and process disposal close the session stream. If the stream fails unexpectedly, mark the local connection unavailable and reject subsequent requests; do not reconnect, replay scripts, or reopen the entry dialog automatically. A new explicit connect establishes a fresh session. Session expiry also prevents further unary calls using its credential. Already accepted executions continue; losing a client does not cancel work or reset PowerShell state. A later admitted client can inspect retained UUID results, subject to normal retention limits.

Admission is enforced at the RC rather than inferred from relay TCP sockets: HTTP/2 can reconnect or open multiple sockets, and unauthenticated connections must not lock out the owner. The relay limits resource use but is not the authority for exclusive diagnostic ownership.

Write short connection/disconnection messages to the RC console, with timestamp, generated nonsecret session ID, direct/relay mode, and reason. In relay mode do not mislabel the loopback/relay address as the actual remote client. Never log authorization headers, full URLs, lease credentials, script output, or declaration data as part of connection logging. Preserve existing request/approval colors and output behavior. MCP stdout remains protocol-only.

## Distribution and executable layout

The Windows workflow keeps its existing Windows native tests, extracted-package smoke tests, and separate client/server release uploads; the macOS job and macOS asset prerequisites are removed. Historic release assets are not deleted. The server's assembly/apphost name becomes `powershell-rc`; update help, publishing checks, test launch paths, documentation, and extracted-package CI accordingly.

Prefer a supported .NET publish layout that keeps the executable at the package root and dependencies/resources below a support directory. First evaluate standard single-file publishing, without trimming, while retaining any necessary engine files in a support directory and documentation under docs. PowerShell relies on physical assembly/module paths, so simply moving DLLs or claiming single-file compatibility is unacceptable. All packaged engine tests must pass from a fresh extraction, with a different working directory and no installed PowerShell/.NET. The client dialog subprocess and module discovery must also work from the packaged layout.

The owner described the layout as desirable if possible. This design does not promise a custom native launcher or adopt an unverified relocation. If standard publishing cannot satisfy the engine's path requirements reliably, retain the required runtime/engine layout and document the limitation; report the evidence before treating the layout objective as delivered. Do not add a custom bootstrapper without revisiting the design. Standard single-file extraction behavior and tradeoffs are documented by Microsoft: https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview .

## Sequential milestones and acceptance

Each milestone targets roughly 2–3 hours of skilled implementation; adjust estimates based on observed compatibility constraints, without collapsing the validation gates.

1. Windows distribution and naming MVP: Windows-only workflow; new server executable name; standard-layout feasibility check; updated docs and fresh-extraction Windows smoke tests. Existing direct diagnostics remain usable.
2. Exclusive diagnostic sessions: authenticated atomic admission, heartbeat/cleanup, client lifecycle, secret-free connection logs. Tests cover simultaneous connect races, rejected second client, explicit disconnect, crash/expiry, failed connect cleanup, late cleanup callbacks, long-running work, and reconnection without replay.
3. Relay allocation and reverse transport: integrated relay mode, pinned registration, owned port allocation, authenticated per-socket reverse streams, bounded resources and deterministic cleanup. Tests cover concurrent allocation, unavailable/exhausted ports, wrong credentials, cross-registration tunnel claims, oversized frames, broken streams and RC termination.
4. Full relay diagnostics and releases: real MCP client -> allocated relay port -> RC TLS -> embedded PowerShell. Assert valid pin success, wrong pin/token failure, one-client admission, output/search/cancellation, stable port across client reconnect, old stream cleanup before port reuse, relay/RC shutdown, startup without PowerShell initialization in relay mode, extracted Windows package behavior, and Windows-only release asset requirements.

Run focused tests during each milestone and the full suite before completion. Native Windows CI provides packaged runtime evidence; macOS source tests alone do not validate Windows native packaging. Actual Windows 10 Pro 22H2 and Server 2016 acceptance remains a separate explicit qualification step.

## Non-goals and remaining builder decisions

No terminating proxy, persistent relay registrations across process restarts, multi-client observation, automatic script replay, Linux distribution, new macOS release builds, externally managed tunnel dependency, production deployment, or automatic firewall changes. The relay must be reachable on its control port and public range; document those operator responsibilities.

The builder selects concrete message/type names, synchronization primitives, test fixtures and the viable standard publish mechanism within the constraints above. Transport loss bounds are liveness targets, not a claim of instant failure detection across a partition.
