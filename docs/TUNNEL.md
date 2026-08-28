# Wisper ⇄ wisp-agent Tunnel Protocol

**Status:** Draft / v0 · **Companion to:** [`DESIGN.md`](./DESIGN.md) (§5–§7) · **Transport:** one persistent raw WebSocket per host, agent-initiated (outbound)

This spec defines the wire protocol between a **host's `wisp-agent`** (Go client, dials out) and the **Wisper manager** (C#/ASP.NET Core Kestrel server). One WebSocket per host carries *all* traffic for that host — many concurrent leases, shells, and exec streams — multiplexed over the single connection. Wisper never dials the host; the host's outbound connection is the only channel.

---

## 1. Roles & invariants

- **Wisper = server + initiator.** Every lease operation (create, exec, shell, release) originates at Wisper (driven by an authenticated, paid consumer request). Wisper **owns the id space** for streams and requests — the agent never allocates ids. This removes any id-collision negotiation.
- **wisp-agent = client + bridge.** The agent is dumb about money and consumers. It translates tunnel frames into calls against its **local wisp** (`wispd`) over wisp's normal HTTP/WS API, and relays results back. It enforces nothing about billing; wisp's `WISP_CONFIG` caps blast radius on the host.
- **Wisper is the metering authority.** The tunnel carries lifecycle *events*; Wisper timestamps them. Host-reported times are advisory (see `DESIGN.md` §2, §11).

## 2. Transport & framing

Raw WebSocket (`System.Net.WebSockets` on Kestrel; `gorilla/websocket` or `nhooyr/coder` on the agent). Two frame classes, distinguished natively by the **WebSocket opcode**:

- **Text frames = control messages.** UTF-8 **JSON**, one message per frame. (JSON for v0 debuggability; MessagePack is a later optimization — §16.)
- **Binary frames = stream bytes.** Raw PTY/exec bytes with a compact fixed header — never JSON-wrapped, so terminal throughput stays cheap.

### Binary frame layout (6-byte header + payload)

```
 byte 0      uint8    ver     protocol/flags, currently 0x01
 byte 1      uint8    ch      channel: 0=stdin/pty-in, 1=stdout/pty-out, 2=stderr
 bytes 2..5  uint32   sid     stream id (big-endian)
 bytes 6..N  bytes    data    raw payload
```

- Max **payload 32 KiB** per binary frame; the sender chunks larger output into multiple frames. Bytes within one `sid` are ordered (WebSocket-over-TCP); no ordering is implied across `sid`s.
- Control-frame JSON should stay small (< 64 KiB); large data never travels as control.

### Identifiers

| Field | Type | Meaning | Allocated by |
|---|---|---|---|
| `rid` | uint32 | **request id** — correlates a request control frame with its response | Wisper (monotonic per connection) |
| `sid` | uint32 | **stream id** — a long-lived byte stream (shell or exec-stream) | Wisper |

Agent-initiated *unsolicited* frames (`host.heartbeat`, `lease.ready`, `lease.ended`) carry neither (`lease.ready` correlates by `leaseId`). Agent *responses* echo the `rid`/`sid` Wisper sent.

## 3. Connection lifecycle

```
agent                                   Wisper
  │  WS handshake  Authorization: Bearer <host-token>
  │─────────────────────────────────────────▶│  validate token, else close 4401
  │◀─────────────────────────────────────────│  101 Switching Protocols
  │  {t:"hello", proto, agentVersion,         │
  │   wispVersion, capability:{images,        │
  │   default, limits, os,                    │
  │   isolation_levels, default_isolation,    │
  │   gpu?}}                                   │
  │─────────────────────────────────────────▶│  register host, mark online,
  │◀─────────────────────────────────────────│  {t:"hello.ack", proto, sessionId,
  │                                           │   pingIntervalMs, maxFrameBytes,
  │                                           │   initialWindowBytes, graceSeconds}
  │   ══ steady state: leases, streams, ══    │
  │   ══ heartbeat (15s), ping/pong (30s) ══  │
```

1. **Dial + auth.** The agent opens `wss://<wisper-host>/agent` and sends the **host agent token** as an `Authorization: Bearer` header (a native WS client can set headers, unlike a browser). The socket is accepted first and the token checked after the upgrade: bad/missing/revoked token → Wisper closes with a **4401** close code before any frames. The token is resolved by a constant-time SHA-256 hashed lookup against `hosts.agent_token_hash` (Postgres, or the in-memory store on a DB-less boot); a token the store does not know falls through to the `Tunnel:HostTokens` config allow-list (raw token → host id), which is honoured **only in the `Development` environment** and fails closed everywhere else. A non-WebSocket request to `/agent` is a plain `400`.
2. **`hello` / `hello.ack`.** The agent advertises its capability (the wisp `GET /images` document: `images[]`, `default`, `limits`, `os`, the effective `isolation_levels` / `default_isolation`, and the optional `gpu` and `capacity` blocks, tasks #521/#571) plus versions. The first frame must be a text `hello` of at most 64 KiB, else **4409**. Wisper replies with the protocol version, a `sessionId` (`sess_<hex>`), and operational params (`pingIntervalMs`, `maxFrameBytes`, `initialWindowBytes`, `graceSeconds`). Prices live in Wisper, never here (`DESIGN.md` §1, §12). Registration supersedes any prior tunnel for the same host (§16); a reconnect within grace also cancels the pending grace timer (§8). Note that `agentVersion`/`wispVersion` and the top-level `capacity` are read but **not persisted** to the `hosts` row today (`DATA_MODEL.md` §4).
3. **Steady state.** Multiplexed lease ops + byte streams + `host.heartbeat` + ping/pong. A relay call that arrives while a freshly connected agent is still completing this handshake waits up to `Tunnel:HostReadinessTimeoutMs` (default 2000 ms) for readiness before failing `host_offline`.
3a. **Presence.** Once registered and `hello.ack` is sent, Wisper persists the advertised `isolation_levels`/`default_isolation` and (when a `gpu` block is present) `gpu_classes`/`gpu_count` regardless of the gate, then flips the host `online` **if** it clears the earning gate: the owner is Connect-enabled, or every enabled `host_image` is priced at `0` cents/min (`PAYMENTS.md` §5). A Connect-incomplete host with a priced image, or an admin-suspended host, stays `offline` (the agent is still connected and may test). `hosts.last_seen_at` is stamped at this flip (and at the offline flip, §8), not on every heartbeat. This is the wiring that makes a live agent's host appear in the consumer catalog.
4. **Close.** Graceful (WebSocket close frame with a code) or dead (missed pongs → §7). On a durable close Wisper marks the host `offline` and applies the disconnect policy (§8). A momentary blip resolved by a reconnect within grace, or a supersede (a new tunnel replacing the old for the same host), keeps the host `online` throughout.

### Close codes

| Code | Meaning |
|---|---|
| 1000 | normal shutdown (either side) |
| 4401 | bad / missing / expired host token — reauth needed |
| 4403 | *reserved* — host suspended by admin. Not sent today: a suspended host's tunnel stays up; suspension is enforced by presence never flipping the host `online` (§3a) |
| 4409 | protocol version incompatible (see `hello`) — also sent when the first frame is missing or not a valid `hello` |
| 4408 | liveness timeout (no frame within the inactivity window — §7) |
| 4402 | host token **revoked** mid-session (agent must not auto-reconnect until re-provisioned) |

## 4. Protocol versioning

`hello.proto` is an integer (starts at `1`). Today Wisper speaks exactly version `1`: a `hello.proto` that is not `1` is closed **4409** (no negotiation is implemented yet; `hello.ack.proto` always echoes `1`). The same `1` is the `ver` byte of every binary frame. New frame types are additive; a receiver **ignores unknown control `t` values**: today they are silently dropped with a debug log (no `error{code:"unsupported"}` reply is implemented), and malformed control JSON is likewise dropped with a warning. This lets agent and manager roll independently.

## 5. Control frame catalog

All control frames are `{ "t": "<type>", ... }`. Direction: **W→A** Wisper→agent, **A→W** agent→Wisper.

### Connection & health

| Dir | `t` | Fields | Notes |
|---|---|---|---|
| A→W | `hello` | `proto, agentVersion, wispVersion, capability{images[],default,limits,os,isolation_levels,default_isolation,gpu?{supported,devices[{id,class,vram_mb}],max_gpus,isolations[]},capacity?{max_contracts,active_contracts,total_cpus,used_cpus,total_memory_mb,used_memory_mb}}, capacity{maxLeases,maxStreams}` | first frame after upgrade; the **top-level** `capacity{maxLeases,maxStreams}` is advisory today — it is parsed but **not enforced**. `capability.isolation_levels` / `default_isolation` are the host's effective isolation posture (from wisp `GET /images`), surfaced in `GET /v1/catalog`. `capability.gpu` (task #521) carries the host's advertised GPU; the manager persists the distinct device `class` strings as `hosts.gpu_classes` and the device count as `hosts.gpu_count` (an absent block leaves previously persisted values untouched), treating class strings as **opaque**. `gpu.max_gpus` is **live-only**: it exists on the in-memory capability snapshot and is the ceiling offer upserts are validated against, never persisted. `gpu.isolations[]` is accepted but currently **dropped** (not snapshotted, surfaced, or validated) — wisp is the enforcer of GPU-per-isolation. A missing `gpu` block ⇒ `supported=false` (older agents keep working). **`capability.capacity` (task #571)** is wisp's real contract-capacity block (snake_case): only `max_contracts` drives a manager decision — it is the host's concurrent-contract ceiling the manager **fast-fails admission against** (a `lease.create` for a host whose live non-terminal lease count has reached it is refused with `at_capacity` before any hold or frame; `> 0` = a limit, `0`/absent = unlimited). Like `gpu.max_gpus` it is **live-only** (in-memory snapshot, never persisted) and refreshes with the capability on reconnect. `active_contracts`/`total_cpus`/`used_cpus`/`total_memory_mb`/`used_memory_mb` are informational (the manager counts its own leases); the ceiling and the live count are surfaced as `at_capacity`/`active_leases`/`max_leases` in `GET /v1/catalog`. wisp stays the authoritative enforcer (see the `at_capacity` failure code in §12) |
| W→A | `hello.ack` | `proto, sessionId, pingIntervalMs, maxFrameBytes, initialWindowBytes, graceSeconds` | operational params: liveness cadence (§7), max binary payload, per-stream flow window (§9), disconnect grace (§8) |
| A→W | `capability.update` | `capability{...}, capacity{...}?` | *reserved, not implemented* — the frame type constant exists but nothing sends or handles it (an incoming one is dropped). Mid-session capability refresh happens **only** via `host.heartbeat.capability` below |
| A→W | `host.heartbeat` | `leases:[{leaseId,wispContractId,status}], load?{cpu,mem,running}, capability?{images[],default,limits,os,isolation_levels,default_isolation,gpu?,capacity?}, status?` | every ~15s; drives reconciliation (§8). Optional `capability` carries the **full** `hello.capability` shape and lets a host refresh its offered isolation, GPU (task #521), and — importantly — its live `capacity.max_contracts` ceiling and per-lease `limits` mid-session (task #61): the fresh snapshot replaces the one the registry has been serving, so the next `lease.create` sees the new capacity within one heartbeat with no reconnect. An **omitted** `capability` (e.g. the agent's local wisp is unreachable) means "no update — keep last known"; it never clears or zeroes the stored snapshot. Optional top-level **`status`** (task #62) is the agent's self-reported health: `"degraded"` when the agent cannot reach its local wisp (the tunnel is up but every downstream `lease.create` would fail), any other value (or an omitted field) is normal. The manager tracks the degraded set in shared storage (visible to every instance) — a degraded host is **excluded from new lease placement** (dropped from the consumer catalog, and `lease.create` fails fast with `host_offline` 409 before any tunnel frame or wallet hold), and the next non-degraded heartbeat restores placement automatically. **Existing leases are untouched** — the containers may still be running fine; lease state continues to be governed solely by the heartbeat lease set-diff. Degraded/restore transitions are logged once per transition, not per heartbeat |
| both | `error` | `rid?, sid?, code, message` | generic failure for a request/stream; agent-sent errors are mapped to consumer-facing codes (§12) |

### Lease lifecycle

| Dir | `t` | Fields | Notes |
|---|---|---|---|
| W→A | `lease.create` | `rid, leaseId, image, network, isolation, resources{cpus,memory_mb,pids,gpus}, ttl_seconds, userdata?, env?` | Wisper has already authorized + billing-gated. `isolation` is the resolved ordered level (`shared`<`sandboxed`<`vm`, defaults `shared`); the agent forwards it to wisp, which re-validates as the real security boundary. `resources.gpus` (task #522) is the count of whole exclusive GPUs, forwarded verbatim — wisp allocates and enforces. `env?` is an optional, opaque `{string:string}` map of create-time environment vars (omitted when absent) |
| A→W | `lease.accepted` | `rid, leaseId, wispContractId, status:"provisioning"` | agent called wisp `POST /contracts` |
| A→W | `lease.ready` | `leaseId` | wisp reached `ready`; **Wisper starts the meter here** |
| A→W | `lease.failed` | `rid, leaseId, code?, error` | provisioning/pull failed; nothing billed. Optional `code` is mapped like an `error` frame's code (§12): `at_capacity` surfaces as `409 at_capacity`, anything else (or none) as `502 lease_failed`. Fails whichever awaiter is outstanding (the `rid` one before `lease.accepted`, the `leaseId` one after) |
| W→A | `lease.release` | `rid, leaseId` | consumer released / admin force-end / failed-create teardown / orphan teardown (§8) |
| A→W | `lease.released` | `rid, leaseId` | agent called wisp `DELETE`; container gone |
| A→W | `lease.ended` | `leaseId, reason:"expired"\|"failed"\|"gone"` | **unsolicited**: wisp's local reaper/TTL ended it. Wisper ends the lease on receipt (§8): `expired` maps to `end_reason = expired`, any other value to `container_lost`; an already-terminal lease is a no-op. A create still awaiting `lease.ready` for that id fails `502 lease_failed` |

### Exec (sync — no byte stream)

| Dir | `t` | Fields | Notes |
|---|---|---|---|
| W→A | `exec.run` | `rid, leaseId, command` | agent calls wisp `POST /exec` |
| A→W | `exec.result` | `rid, stdout, stderr, exit_code` | fully buffered |

### Exec (streamed) & shell — open a byte stream `sid`

| Dir | `t` | Fields | Notes |
|---|---|---|---|
| W→A | `exec.open` | `rid, sid, leaseId, command` | agent calls wisp `POST /exec?stream=1` |
| A→W | `exec.opened` | `rid, sid` | then binary frames flow A→W on `sid` (ch 1/2) |
| A→W | `exec.exit` | `sid, exit_code` | stream complete |
| W→A | `shell.open` | `rid, sid, leaseId, cols, rows` | agent opens wisp `WS /contracts/:id/shell` |
| A→W | `shell.opened` | `rid, sid` | then binary frames flow **both** ways on `sid` (ch 0 in, ch 1 out) |
| W→A | `shell.resize` | `sid, cols, rows` | window resize (agent forwards to the PTY) |
| both | `stream.credit` | `sid, bytes` | replenish the peer's send window for `sid` as bytes are drained downstream (§9) |
| W→A | `stream.close` | `sid` | consumer aborted / disconnected → agent cancels the exec/shell |
| A→W | `stream.closed` | `sid, reason` | stream torn down (peer exit, error, or `flow_violation` §9) |

## 6. Byte streams

- A **shell** is bidirectional over one `sid`: `ch=0` carries consumer keystrokes W→A→pty-stdin; `ch=1` carries pty output A→W→consumer. A TTY has no stderr split (matches wisp's `ExecShell`, which is a single raw stream).
- A **streamed exec** is unidirectional A→W: `ch=1` stdout, `ch=2` stderr, terminated by an `exec.exit` control frame.
- `stream.close` (W→A) is how a consumer disconnect/abort cancels work on the host; the agent closes the underlying wisp shell/exec and replies `stream.closed`.

## 7. Keepalive: ping/pong + heartbeat

Two independent mechanisms:

- **Liveness (inactivity timer).** The server sets the protocol-level keep-alive (`pingIntervalMs`, 30s) but does not count pongs. Liveness is an **application inactivity window**: if *no frame of any kind* arrives within the timeout (default 2.5 × ping ≈ 75 s), the peer is presumed dead: close **4408**, agent reconnects (§8). The ~15 s heartbeat guarantees a healthy agent always beats the window. This also keeps NAT bindings and the **load-balancer idle timeout (e.g. 900 s)** alive with wide margin.
- **Application heartbeat (state).** `host.heartbeat` every ~15s carries the host's live lease list + load. It is *not* liveness (ping/pong is) — it is the truth source Wisper reconciles against after a reconnect and the signal for host load/health in the UI.

## 8. Disconnect, grace, reconnect & resync

Tunnel loss must be handled without either **billing a consumer through a blind window** or **destroying a healthy lease over a momentary blip**. Both are solved by a **grace window with paused billing**: the lease survives a brief disconnect, the meter stops during it, and only a *sustained* loss ends the lease.

**Reconnect (agent side):** on any drop the agent reconnects with **exponential backoff + jitter** (250 ms → cap 15 s) and re-sends `hello` with current capability and its live lease list. Backoff is short because the grace window is bounded.

**Grace window (Wisper side):** when a host's tunnel drops, Wisper moves that host's active leases to `suspended` and starts a **grace timer** (`graceSeconds`, default 90 s, operator-configurable):
- **Billing pauses immediately** at the last healthy liveness point — nothing flows, nothing is metered, the host earns nothing for the gap. The meter only ever accrues over intervals bracketed by healthy ping/pong (§7), so a blind window is *structurally* un-billable, not just un-billed.
- The **container keeps running on the host**; wisp's local reaper still enforces the lease TTL, so nothing runs unpaid past its bound even if Wisper never hears back.
- **Consumer streams are torn down** (the socket is gone) — the consumer's shell/exec must be reopened — but the **lease and its container persist**, so no work is lost.

**On reconnect within grace:** Wisper set-diffs the agent's reported live leases (`hello` + first `host.heartbeat`) against its `suspended` set:
- Lease still present and healthy on the host → **resume**: back to `active`, **billing restarts**, consumer may reopen streams. The lease keeps its id, price snapshot, and usage ledger (leaseIds are Wisper-issued and stable across reconnects).
- Lease no longer on the host (container died in a host crash/restart) → **end** it (`end_reason = container_lost`), finalize at last-healthy time.

**On grace expiry (no reconnect):** end all that host's suspended leases (`end_reason = host_disconnect`), finalize billing at last-healthy time, mark the host `offline` (so the catalog drops it), stamping last-seen at last-healthy. wisp's TTL guarantees the abandoned containers are reaped regardless. A tunnel that closes with **no leases to protect** has nothing to wait for, so the host is marked `offline` immediately rather than arming an empty grace window. Marking `offline` never clears an admin **suspension** — a suspended host stays suspended.

**On reconnect after grace expiry (post-grace path):** an operator restart or a prolonged outage may push the reconnect past the grace window, so the manager has already ended the leases as `host_disconnect`. However, the **containers are still running on the host** (wispd/agent stops, not the containers). If the agent's first `host.heartbeat` after reconnect reports live contracts that map to those `host_disconnect`-ended leases, Wisper **revives** them: back to `active`, `end_reason`/`ended_at` cleared, meter watermark reset to the reconnect instant so the offline gap is never billed, same lease id. A paid lease is re-held for its remaining time first (`PAYMENTS.md` §4); if the wallet cannot cover the revival hold the lease is ended `payment_failed` instead. This prevents a permanent desync where the catalog advertises free capacity while the host is actually full, which would cause a new create to be green-lit by the manager guard but rejected by wisp with `at_capacity`. Contracts the agent reports that have no revivable lease (not found, ended for another reason, or TTL already expired) are orphaned; wisp's TTL reaper reclaims those containers regardless.

**Durable grace (restart-safe).** The in-process grace timer lives only in memory, so a manager restart, crash or scale-in with a host inside grace would strand its leases in `suspended` forever. Two backstops close that gap:
- `leases.suspended_at` is stamped (wall-clock) on every suspend and cleared on resume/revive/end (`DATA_MODEL.md` §5). A **suspension sweep** runs on every instance every 30 s (only when a database is configured and `Metering:Enabled`, the same gates as the meter) and ends, as `host_disconnect` finalized at `suspended_at`, every lease still `suspended` for longer than `graceSeconds` + a 30 s safety margin whose host has no armed in-process timer on that instance. Every `suspended → ended` transition is a CAS on `status = 'suspended'`, so two instances (or a sweep racing a late timer or a heartbeat) converge on exactly one end and one hold release.
- **Continuous heartbeat reconciliation.** Every `host.heartbeat` that is not the first beat inside a grace window is set-diffed against the manager's `active + suspended` set for the host: an `active` lease the host no longer reports is ended `container_lost` (billing finalized at the beat, TTL-capped) and its hold released; a `suspended` lease (a stranded post-restart row) that the host reports is resumed, and one it no longer reports is ended `container_lost` at its `suspended_at`; a reported id whose lease was ended `host_disconnect` is revived (same rules as above); a reported id whose lease is terminal for any other reason is a **terminal orphan** and Wisper best-effort relays a `lease.release` for it, once per connection (so a stable orphan does not spam relays or logs); a reported id with **no manager row at all** is left alone, because a create inserts the row only after `lease.ready` and a beat can land in that window (wisp's TTL reaper is the backstop). The steady-state case (reported set equals the active set, nothing suspended) does zero writes.
- **Resume vs revive.** A resume (`suspended → active`) keeps the existing hold; every resume is CAS-guarded on `suspended`, and if the sweep won the race in between, the lease is instead revived through the ended→active path with a fresh hold, so an `active` paid lease always has a hold behind it.

**Other end drivers.** A consumer `DELETE` (`released`), an unsolicited `lease.ended` (`expired`/`container_lost`), and an admin force-end (`admin`, `API.md` §8) all take the same three steps: finalize billing (TTL- and last-healthy-capped), CAS transition to `ended` on the status that was read, release the hold. A concurrent driver that loses the CAS does nothing further, so `end_reason` reflects whichever cause won.

Reconciliation is an idempotent set-diff run on every reconnect and every heartbeat, so repeated flaps converge correctly.

## 9. Flow control & backpressure

Many streams share one socket, so a single fast producer (a container spewing stdout) must never exhaust manager memory or head-of-line-block other streams. Two layers, both always on:

**Layer 1 — per-stream credit/window (application flow control).** Every byte stream (`sid`) has an independent send window, exactly as HTTP/2 and yamux/smux do:
- On open the receiver grants an initial window (`initialWindowBytes`, default 256 KiB, announced in `hello.ack`).
- A sender may have at most `window` unacknowledged bytes in flight on a given `sid`; when the window hits 0 it **stops sending on that sid** — and, being a bridge, propagates that stall inward (a shell stops reading the PTY; an exec stops reading the wisp SSE stream), which backpressures the container itself.
- As the receiver drains bytes downstream (writes them to the consumer's WebSocket, or to container stdin), it replenishes with a `stream.credit{sid, bytes}` control frame. Wisper batches its credits: it emits one `stream.credit` once at least half the initial window (128 KiB by default) has been drained since the last grant.
- This bounds per-stream memory to one window and guarantees no single `sid` can starve the shared socket — independent of, and finer-grained than, TCP.

**Layer 2 — TCP backpressure (transport safety net).** The socket is still self-throttling underneath: if a peer stops reading, the other's writes block, propagating to the container. Layer 1 makes throttling *fair across streams*; Layer 2 is the floor that protects the process even if credit accounting is momentarily behind.

**Overflow is a protocol fault, not a silent drop.** If a peer sends past its granted credit, the offending stream is closed with `stream.closed{reason:"flow_violation"}` and logged. Normal operation **never drops bytes** — it slows the producer. (This is the correctness guarantee that separates this from "best-effort buffering.")

## 10. Agent ⇄ local wisp mapping

The agent is a thin translator. Each tunnel op maps to a wisp API call (`--wisp http://127.0.0.1:8080`, `--wisp-token <app-token>`; per-contract tokens stay agent-internal):

| Tunnel | wisp call |
|---|---|
| `lease.create` | `POST /contracts {image,network,isolation,resources(incl. gpus),ttl_seconds,userdata,env}` → keep `{contract_id, token}`; emit `lease.accepted`, then `lease.ready` when status hits `ready` |
| `exec.run` | `POST /contracts/:id/exec {command}` (Bearer contract token) → `exec.result` |
| `exec.open` | `POST /contracts/:id/exec?stream=1` → parse SSE, emit binary frames + `exec.exit` |
| `shell.open` | `WS /contracts/:id/shell?token=<contract token>` → pipe bytes ↔ `sid` |
| `shell.resize` | forward to the wisp shell (resize control) |
| `lease.release` / `stream.close` | `DELETE /contracts/:id` / close the exec/shell |
| TTL expiry seen locally | wisp status → `expired` ⇒ emit `lease.ended` |

Because the agent only speaks wisp's public API, **wisp needs no changes** for the marketplace — the agent is just another wisp client.

## 11. Consumer API ⇄ tunnel mapping

The consumer never touches the tunnel; Wisper relays (routing across instances via the Redis backplane, `DESIGN.md` §7, which also lists the Redis keys and channels; cross-request state rule: `DESIGN.md` §7):

| Consumer call (Wisper) | Tunnel |
|---|---|
| `POST /leases` (after auth + admission + wallet gate) | `lease.create` → wait `lease.accepted` then `lease.ready` |
| `POST /leases/:id/exec` | `exec.run` → `exec.result` |
| `POST /leases/:id/exec?stream=1` (SSE) | `exec.open` → stream binary frames back as SSE |
| `WS /leases/:id/shell` (xterm) | `shell.open` → bridge the consumer WS ⇄ `sid` binary frames |
| `DELETE /leases/:id` | `lease.release` → wait `lease.released` (a host with no live tunnel is treated as already released; the lease is ended locally) |
| `POST /v1/admin/leases/:id/end` | best-effort `lease.release` after the ledger-side end |
| failed create (hold could not post) | `lease.release` teardown of the just-provisioned contract |

## 12. Errors & timeouts

- Every `rid` request has one Wisper-side **deadline**, `Tunnel:RelayRequestTimeoutMs` (default 120 s, sized to cover an image pull), applied to `lease.create` (both the `lease.accepted` and the `lease.ready` waits), `exec.run`, `lease.release`, `shell.open` and `exec.open`. On timeout Wisper fails the consumer call with `upstream_timeout` (504); no cleanup frame is sent automatically. A request routed to another instance over the backplane is bounded by `Tunnel:Backplane:RpcTimeoutMs` (default 120 s) the same way. A tunnel that closes mid-request fails every pending waiter with `host_offline`.
- `error{rid,code,message}` carries typed failures. Wisper maps agent-reported codes to consumer errors: `not_ready` (lease not `ready`) → `lease_not_ready` (409), `unknown_lease` → `not_found` (404), and `at_capacity` → `at_capacity` (409) are recognized and mapped to their consumer equivalents; **any other code** (e.g. a wisp non-2xx) collapses to `lease_failed` (502). There is no distinct `unsupported`/`wisp_error`/`overflow`/`internal` handling. The `at_capacity` mapping is the authoritative backstop for per-host admission (task #571): the manager fast-fails a `lease.create` against a host at its advertised `capability.capacity.max_contracts` (§5), but wisp remains the enforcer — if it rejects a create in the admit→provision race it reports `at_capacity`, which surfaces to the consumer as the same `409 at_capacity` (and the failed-create teardown still runs).
- Malformed control frames (bad JSON) are dropped with a warning; unknown binary `sid`s / flow violations tear the stream down (§9). No `error` reply is emitted for malformed input today.

## 13. Security

- **Host token** in the handshake header; hashed at rest, revocable, rotatable. Revocation closes the tunnel (4402) and forbids auto-reconnect.
- The **relay is mandatory** — consumers never get a wisp contract token or a host address, so they cannot bypass metering (`DESIGN.md` §2, §15).
- TLS end-to-end (terminated at the load balancer). The agent also holds the local wisp app token; per-contract tokens never leave the agent.
- Wisper authorizes + wallet-gates **before** emitting `lease.create`; wisp `WISP_CONFIG` independently caps each lease on the host (defense in depth).
- **TODO (harden):** `env` is plaintext v1 for local/trusted use; production must deliver secret env as a secret (e.g. tmpfs+stdin at the wisp/docker layer) and must never log values.

## 14. Worked example — lease → shell → release

```
consumer            Wisper                         agent            wisp / container
  │ POST /leases ──▶ │ auth+wallet ok               │                 │
  │                  │ rid=7 ─ lease.create ───────▶ │ POST /contracts │
  │                  │                               │ ───────────────▶│ boot (pull if needed)
  │                  │ ◀─ lease.accepted(rid=7) ──── │ ◀── 201 ────────│
  │                  │ ◀─ lease.ready ────────────── │ ◀── ready ──────│
  │ ◀── 201 lease ── │  ▶ START METER                │                 │
  │ WS /shell ─────▶ │ sid=3 ─ shell.open ─────────▶ │ WS /shell ─────▶│ PTY
  │  keystrokes ───▶ │  ═ bin ch0 sid3 ═════════════▶│ ══ stdin ══════▶│
  │ ◀── output ───── │ ◀═ bin ch1 sid3 ═════════════ │ ◀══ stdout ═════│
  │ (ping/pong 30s, heartbeat 15s throughout)        │                 │
  │ DELETE /leases ─▶│ rid=9 ─ lease.release ──────▶ │ DELETE ────────▶│ destroy
  │                  │ ◀─ lease.released(rid=9) ──── │ ◀── 200 ────────│
  │ ◀── 200 ──────── │  ▶ STOP METER, finalize usage → ledger (hold release) │
```

## 15. Frame reference (quick)

- Control = **WS text**, JSON `{t, rid?, sid?, ...}`.
- Data = **WS binary**, `[ver:1][ch:1][sid:4][payload]`, ≤ 32 KiB payload.
- Liveness = **WS ping** every 30 s (`pingIntervalMs`) plus an application inactivity window of 2.5 × ping (about 75 s, `Tunnel:LivenessTimeoutMs` to override); no frame of any kind within the window closes the tunnel 4408 (§7).
- Wisper allocates all `rid`/`sid`; agent echoes them.

## 16. Deliberate scope boundaries

Everything required for a correct, production-grade tunnel is specified above and built in full: multiplexed framing, per-stream credit flow control (§9), grace-window reconnect with paused billing (§8), liveness (§7), host-capacity enforcement (§5), typed errors + timeouts (§12), and security (§13). The items below are **settled decisions**, not deferred work.

**Settled — chosen, not punted:**
- **JSON control frames** (not MessagePack). The control channel is low-volume — a handful of small frames per lease op; the high-volume path (PTY/exec bytes) is already raw binary (§2). JSON's debuggability wins on the control plane; MessagePack would optimize a channel that isn't the bottleneck.
- **`permessage-deflate`** — *designed but not implemented*: nothing negotiates compression today (the accept sets only the keep-alive interval). The design intent stands — enable for text (control) frames and exec/log byte streams, disable for the shell channel (interactive PTY is latency-sensitive; compression adds head-of-line latency) — but note the per-channel split needs a different accept API than the one in use.

**Out of scope — because it isn't needed for correctness, not because it's hard:**
- **Multiple concurrent tunnels per host (redundancy).** One tunnel per host with fast reconnect (§8) and a bounded grace window is the standard, sufficient model; a second live tunnel per host adds split-brain/ordering complexity for redundancy that reconnect already provides. A new connection from a host **supersedes** the prior one (the old socket is closed 1000); this cleanly covers rolling agent restarts. If a concrete availability requirement ever demands hot-standby tunnels, it is an additive change (a tunnel-generation id in `hello`), not a redesign.
```
