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

Agent-initiated *unsolicited* frames (`host.heartbeat`, `lease.ended`) carry neither. Agent *responses* echo the `rid`/`sid` Wisper sent.

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
  │                                           │   pingIntervalMs, maxFrameBytes}
  │   ══ steady state: leases, streams, ══    │
  │   ══ heartbeat (15s), ping/pong (30s) ══  │
```

1. **Dial + auth.** The agent opens `wss://<wisper-host>/agent` and sends the **host agent token** as an `Authorization: Bearer` header (a native WS client can set headers — unlike a browser). Bad/missing/revoked token → Wisper closes with a **4401** close code before any frames.
2. **`hello` / `hello.ack`.** The agent advertises its capability — the wisp `GET /images` document (`images[]`, `default`, `limits`, `os`, the effective `isolation_levels` / `default_isolation`, and the optional `gpu` block — task #521) plus versions. Wisper replies with the negotiated protocol version, a `sessionId`, and operational params (`pingIntervalMs`, `maxFrameBytes`). Prices live in Wisper, never here (`DESIGN.md` §1, §12).
3. **Steady state.** Multiplexed lease ops + byte streams + `host.heartbeat` + ping/pong.
3a. **Presence.** Once registered and `hello.ack` is sent, Wisper flips the host `online` **if** it clears the earning gate — the owner is Connect-enabled, or every enabled `host_image` is priced at `0` cents/min (`PAYMENTS.md` §5). A Connect-incomplete host with a priced image, or an admin-suspended host, stays `offline` (the agent is still connected and may test). This is the wiring that makes a live agent's host appear in the consumer catalog.
4. **Close.** Graceful (WebSocket close frame with a code) or dead (missed pongs → §7). On a durable close Wisper marks the host `offline` and applies the disconnect policy (§8). A momentary blip resolved by a reconnect within grace, or a supersede (a new tunnel replacing the old for the same host), keeps the host `online` throughout.

### Close codes

| Code | Meaning |
|---|---|
| 1000 | normal shutdown (either side) |
| 4401 | bad / missing / expired host token — reauth needed |
| 4403 | host suspended by admin |
| 4409 | protocol version incompatible (see `hello`) |
| 4408 | liveness timeout (missed pongs) |
| 4402 | host token **revoked** mid-session (agent must not auto-reconnect until re-provisioned) |

## 4. Protocol versioning

`hello.proto` is an integer (starts at `1`). Wisper answers with the highest version it and the agent both support in `hello.ack.proto`; if there is no overlap it closes **4409**. New frame types are additive; a receiver **ignores unknown control `t` values** with an `rid` by replying `error{code:"unsupported"}`, and ignores unknown unsolicited frames. This lets agent and manager roll independently.

## 5. Control frame catalog

All control frames are `{ "t": "<type>", ... }`. Direction: **W→A** Wisper→agent, **A→W** agent→Wisper.

### Connection & health

| Dir | `t` | Fields | Notes |
|---|---|---|---|
| A→W | `hello` | `proto, agentVersion, wispVersion, capability{images[],default,limits,os,isolation_levels,default_isolation,gpu?{supported,devices[{id,class,vram_mb}],max_gpus,isolations[]}}, capacity{maxLeases,maxStreams}` | first frame after upgrade; `capacity` = how much this host will serve concurrently, **Wisper-enforced** (a lease/stream over capacity is refused with `error{code:"at_capacity"}` before it reaches the host). `capability.isolation_levels` / `default_isolation` are the host's effective isolation posture (from wisp `GET /images`), surfaced in `GET /v1/catalog`. `capability.gpu` (task #521) carries the host's advertised GPU; the manager persists the distinct device `class` strings as `hosts.gpu_classes` and the device count as `hosts.gpu_count`, treating class/isolation strings as **opaque** (like `isolation_levels`). A missing `gpu` block ⇒ `supported=false` (older agents keep working) |
| W→A | `hello.ack` | `proto, sessionId, pingIntervalMs, maxFrameBytes, initialWindowBytes, graceSeconds` | operational params: liveness cadence (§7), max binary payload, per-stream flow window (§9), disconnect grace (§8) |
| A→W | `capability.update` | `capability{...}, capacity{...}?` | host changed its wisp allow-list/limits/capacity while online |
| A→W | `host.heartbeat` | `leases:[{leaseId,wispContractId,status}], load?{cpu,mem,running}, capability?{isolation_levels,default_isolation,gpu?}` | every ~15s; drives reconciliation (§8). Optional `capability` lets a host refresh its offered isolation levels — and its `gpu` block (task #521) — mid-session without reconnecting |
| W→A | `error` | `rid?, sid?, code, message` | generic failure for a request/stream |

### Lease lifecycle

| Dir | `t` | Fields | Notes |
|---|---|---|---|
| W→A | `lease.create` | `rid, leaseId, image, network, isolation, resources{cpus,memory_mb,pids}, ttlSeconds, userdata?, env?` | Wisper has already authorized + billing-gated. `isolation` is the resolved ordered level (`shared`<`sandboxed`<`vm`, defaults `shared`); the agent forwards it to wisp, which re-validates as the real security boundary. `env?` is an optional, opaque `{string:string}` map of create-time environment vars (omitted when absent) |
| A→W | `lease.accepted` | `rid, leaseId, wispContractId, status:"provisioning"` | agent called wisp `POST /contracts` |
| A→W | `lease.ready` | `leaseId` | wisp reached `ready`; **Wisper starts the meter here** |
| A→W | `lease.failed` | `rid, leaseId, error` | provisioning/pull failed; nothing billed |
| W→A | `lease.release` | `rid, leaseId` | consumer released / TTL / admin |
| A→W | `lease.released` | `rid, leaseId` | agent called wisp `DELETE`; container gone |
| A→W | `lease.ended` | `leaseId, reason:"expired"\|"failed"\|"gone"` | **unsolicited** — wisp's local reaper/TTL ended it |

### Exec (sync — no byte stream)

| Dir | `t` | Fields | Notes |
|---|---|---|---|
| W→A | `exec.run` | `rid, leaseId, command` | agent calls wisp `POST /exec` |
| A→W | `exec.result` | `rid, stdout, stderr, exitCode` | fully buffered |

### Exec (streamed) & shell — open a byte stream `sid`

| Dir | `t` | Fields | Notes |
|---|---|---|---|
| W→A | `exec.open` | `rid, sid, leaseId, command` | agent calls wisp `POST /exec?stream=1` |
| A→W | `exec.opened` | `rid, sid` | then binary frames flow A→W on `sid` (ch 1/2) |
| A→W | `exec.exit` | `sid, exitCode` | stream complete |
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

- **WebSocket ping/pong (liveness).** Both peers send a protocol-level **ping every 30s** (`pingIntervalMs`) and expect a pong. Missing **2 consecutive pongs (~60–90s)** ⇒ the peer is dead: close **4408**, agent reconnects (§8). This keeps NAT bindings and the **load-balancer idle timeout (e.g. 900 s)** alive with wide margin and detects half-open TCP fast.
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

Reconciliation is an idempotent set-diff run on every reconnect, so repeated flaps converge correctly.

## 9. Flow control & backpressure

Many streams share one socket, so a single fast producer (a container spewing stdout) must never exhaust manager memory or head-of-line-block other streams. Two layers, both always on:

**Layer 1 — per-stream credit/window (application flow control).** Every byte stream (`sid`) has an independent send window, exactly as HTTP/2 and yamux/smux do:
- On open the receiver grants an initial window (`initialWindowBytes`, default 256 KiB, announced in `hello.ack`).
- A sender may have at most `window` unacknowledged bytes in flight on a given `sid`; when the window hits 0 it **stops sending on that sid** — and, being a bridge, propagates that stall inward (a shell stops reading the PTY; an exec stops reading the wisp SSE stream), which backpressures the container itself.
- As the receiver drains bytes downstream (writes them to the consumer's WebSocket, or to container stdin), it replenishes with a `stream.credit{sid, bytes}` control frame.
- This bounds per-stream memory to one window and guarantees no single `sid` can starve the shared socket — independent of, and finer-grained than, TCP.

**Layer 2 — TCP backpressure (transport safety net).** The socket is still self-throttling underneath: if a peer stops reading, the other's writes block, propagating to the container. Layer 1 makes throttling *fair across streams*; Layer 2 is the floor that protects the process even if credit accounting is momentarily behind.

**Overflow is a protocol fault, not a silent drop.** If a peer sends past its granted credit, the offending stream is closed with `stream.closed{reason:"flow_violation"}` and logged. Normal operation **never drops bytes** — it slows the producer. (This is the correctness guarantee that separates this from "best-effort buffering.")

## 10. Agent ⇄ local wisp mapping

The agent is a thin translator. Each tunnel op maps to a wisp API call (`--wisp http://127.0.0.1:8080`, `--wisp-token <app-token>`; per-contract tokens stay agent-internal):

| Tunnel | wisp call |
|---|---|
| `lease.create` | `POST /contracts {image,network,isolation,resources,ttl_seconds,userdata}` → keep `{contract_id, token}`; emit `lease.accepted`, then `lease.ready` when status hits `ready` |
| `exec.run` | `POST /contracts/:id/exec {command}` (Bearer contract token) → `exec.result` |
| `exec.open` | `POST /contracts/:id/exec?stream=1` → parse SSE, emit binary frames + `exec.exit` |
| `shell.open` | `WS /contracts/:id/shell?token=<contract token>` → pipe bytes ↔ `sid` |
| `shell.resize` | forward to the wisp shell (resize control) |
| `lease.release` / `stream.close` | `DELETE /contracts/:id` / close the exec/shell |
| TTL expiry seen locally | wisp status → `expired` ⇒ emit `lease.ended` |

Because the agent only speaks wisp's public API, **wisp needs no changes** for the marketplace — the agent is just another wisp client.

## 11. Consumer API ⇄ tunnel mapping

The consumer never touches the tunnel; Wisper relays (routing across instances via the Redis backplane, `DESIGN.md` §7):

| Consumer call (Wisper) | Tunnel |
|---|---|
| `POST /leases` (after auth + wallet gate) | `lease.create` → wait `lease.ready` |
| `POST /leases/:id/exec` | `exec.run` → `exec.result` |
| `POST /leases/:id/exec?stream=1` (SSE) | `exec.open` → stream binary frames back as SSE |
| `WS /leases/:id/shell` (xterm) | `shell.open` → bridge the consumer WS ⇄ `sid` binary frames |
| `DELETE /leases/:id` | `lease.release` |

## 12. Errors & timeouts

- Every `rid` request has a Wisper-side **deadline** (e.g. `lease.create` 120s to cover image pull, `exec.run` per-command). On timeout Wisper fails the consumer call and may send `stream.close`/`lease.release` to clean up the host.
- `error{rid,code,message}` carries typed failures: `unsupported`, `not_ready` (lease not `ready`), `unknown_lease`, `wisp_error` (local wisp returned non-2xx), `overflow`, `internal`.
- Malformed frames (bad JSON, unknown binary `sid`, oversize) ⇒ `error` + optional close if the connection state is unrecoverable.

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
  │ ◀── 200 ──────── │  ▶ STOP METER, finalize usage → Stripe          │
```

## 15. Frame reference (quick)

- Control = **WS text**, JSON `{t, rid?, sid?, ...}`.
- Data = **WS binary**, `[ver:1][ch:1][sid:4][payload]`, ≤ 32 KiB payload.
- Liveness = **WS ping/pong** every 30s; dead after 2 misses.
- Wisper allocates all `rid`/`sid`; agent echoes them.

## 16. Deliberate scope boundaries

Everything required for a correct, production-grade tunnel is specified above and built in full: multiplexed framing, per-stream credit flow control (§9), grace-window reconnect with paused billing (§8), liveness (§7), host-capacity enforcement (§5), typed errors + timeouts (§12), and security (§13). The items below are **settled decisions**, not deferred work.

**Settled — chosen, not punted:**
- **JSON control frames** (not MessagePack). The control channel is low-volume — a handful of small frames per lease op; the high-volume path (PTY/exec bytes) is already raw binary (§2). JSON's debuggability wins on the control plane; MessagePack would optimize a channel that isn't the bottleneck.
- **`permessage-deflate` enabled** for text (control) frames and exec/log byte streams; **disabled for the shell channel** (interactive PTY is latency-sensitive and low-volume, and compression adds head-of-line latency). Negotiated at handshake.

**Out of scope — because it isn't needed for correctness, not because it's hard:**
- **Multiple concurrent tunnels per host (redundancy).** One tunnel per host with fast reconnect (§8) and a bounded grace window is the standard, sufficient model; a second live tunnel per host adds split-brain/ordering complexity for redundancy that reconnect already provides. A new connection from a host **supersedes** the prior one (the old socket is closed 1000); this cleanly covers rolling agent restarts. If a concrete availability requirement ever demands hot-standby tunnels, it is an additive change (a tunnel-generation id in `hello`), not a redesign.
```
