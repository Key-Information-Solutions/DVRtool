# Device identity — why a successful login is not proof of anything

**Established:** 2026-08-24, prompted by the live-video work: Site A runs more than
one system behind a single IP, separated only by forwarded port, on one shared account.

The question this answers: *"we authenticated, so we're talking to the right recorder —
right?"* No. On a fleet with shared credentials, authentication proves the password is good
**somewhere**; it says nothing about **which** box answered. This doc records the failure
mode, the mechanism DVRTool now uses against it, and the deliberate limits of that
mechanism.

## 0. TL;DR

- **Every login is followed by an identity check.** The first successful login to a
  `host:port` records the device's serial number in
  `%APPDATA%\DVRTool\identities.json`; every later login there must present the same
  serial or DVRTool refuses to act. Trust-on-first-use, exactly like `pins.json` for
  self-signed TLS certificates — and for the same reason.
- **A saved GUI device is bound to its serial**, not just to its address
  (`SavedDevice.ExpectedSerial`). The record is what an export is filed under, so the
  record is what has to mean one specific piece of hardware.
- **Door panels are the sharp end**, and are verified before every read and *both* legs of
  every write. A wrong port on a `roster` is a misleading report; a wrong port on a `grant`
  is a working fob on someone else's building.
- **Panel addresses now carry a port** (`--panels 10.0.0.5,10.0.0.5:8001`). Before this,
  two controllers behind one IP could not even be expressed — and their cards were stamped
  with the bare host, which merged their rosters.
- **A blank serial is reported, never pinned.** Firmware that answers with no serial is
  "unverifiable", which is a caution — not a pass, and not a match against every other
  serial-less device.
- **The fleet is cross-checked too** (`FleetAudit`): two records on one address, or two
  addresses that turn out to be one recorder, are findings no single record's own
  connection test can produce.

## 1. The failure mode

A site forwards several systems through one public address:

```
203.0.113.9:8081  →  Store 3 NVR      (10.1.1.10:80)
203.0.113.9:8082  →  Store 4 NVR      (10.1.1.11:80)
203.0.113.9:8001  →  door controller  (10.1.1.20:8000)
```

Every one of them takes the same `admin` password, because that is how fleets are actually
provisioned. Now:

- A port is typed as `8081` when `8082` was meant. **The login succeeds.** Channels list.
  A stream plays. A three-hour export completes and is filed under "Store 4" holding Store
  3's footage. Nothing anywhere reports a problem.
- The site's router is rebuilt and the two rules come back swapped. Same outcome, with no
  typo to find, and every saved record still "works".
- `dvrtool access grant --panel 203.0.113.9 --card 12345 --doors all --force` reaches a
  controller — just not the one intended. The fob opens four doors in the wrong building,
  and the verification read-back confirms the write succeeded, because it did.

None of this is caught by credentials, by a port check, or by a certificate pin. The
certificate pin is keyed by `host:port` and would happily first-use-pin a re-forwarded
port; most of these devices are on plain HTTP anyway.

What separates the two recorders is not knowledge (the password is shared) but **identity**
— and both vendors hand out a stable one for free: `serialNumber` from Hikvision's
`/ISAPI/System/deviceInfo`, `serialNumber` from Dahua's `magicBox.cgi?action=getSystemInfo`,
and the serial in the login struct on a DS-K panel (which is also the only self-description
those panels give — see `hikvision-access-control-findings.md`).

## 2. The mechanism

`DVRTool.Core/DeviceIdentity.cs`:

| Piece | Job |
|---|---|
| `DeviceAddress` | The canonical `host:port` key, and the parser for operator-typed `ip[:port]`. A bare host is not an address — it names a NAT rule set, not a machine. |
| `DeviceFingerprint` | A device's serial (normalized: whitespace-stripped, upper-cased) plus its model. The model is carried for the operator and never compared; firmware upgrades and renames must not read as a different box. |
| `DeviceIdentityStore` | TOFU pinning of serial per address, in `identities.json`. Read-only except on first contact, so the common path never writes. |
| `IdentityCheck` / `IdentityVerdict` | `FirstContact` / `Match` / `Mismatch` / `Unverifiable`, with the operator-facing prose for each. |
| `DeviceIdentityException` | Thrown on a mismatch. Deliberately **not** an `NvrException`: the paths that treat a device error as "this one is unreachable, carry on" must not carry on here. |
| `DeviceIdentityGuard` | `Check` (over an already-fetched `DeviceInfo`), `CheckAsync`, `EnsureAsync` (throws), and `AddressOf` for both connection types. |
| `FleetAudit` | Cross-record findings: `DuplicateAddress` (error), `SameDevice` (warning), `SharedHost` (info). |

Where it runs:

- **GUI, selecting a device** — `VerifyDeviceAsync` before the channel list loads. A
  mismatch drops the client entirely; every tab downstream would otherwise be pointed at
  the wrong hardware while the list still shows the name that was clicked.
- **GUI, Test connection** — the web probe already fetched device info, so identity costs
  it nothing. A wrong device renders red and the summary refuses to say "all three ports
  reachable". A successful test binds the record to the serial it just saw.
- **GUI, Users tab** — every device in the selected set is identified before its read
  counts, in both modes. A mismatch is carried as that device's failure (its column reads
  "?", with a PARTIAL warning naming it) rather than silently folded into the matrix.
- **GUI, Access tab** — per panel, on every roster read. A *saved* panel
  (`SavedDevice.Kind = "panel"`, added 2026-08-26) is additionally held to its record's
  `ExpectedSerial`, exactly like a saved recorder; the record binds on its first successful
  roster read, because the Add-device dialog's Test deliberately never logs into a panel
  (DS-K lockout).
- **CLI** — `VerifyIdentityAsync` before every command that has credentials to do it with.
  `--expect-serial <s>` asserts a specific serial (for scripts); `--trust-new-device`
  re-pins (for a genuinely replaced recorder).
- **CLI `access`** — `ConnectVerifiedAsync` on every panel connection; `grant` and `revoke`
  call `Ensure`, and `revoke` re-verifies on the write connection rather than trusting the
  read pass, because that is a second login.

## 3. Exit codes and severities

- A wrong device is **not** a port failure, but it maps to `ProbeSeverity.Fail` regardless:
  the port is perfect and the answer is still wrong, and it must never render as a pass.
  `ProbeStatus.WrongDevice` is the only non-port verdict the probe produces.
- `dvrtool test` exits **2** on a wrong device (nothing about it is fixed by opening a
  firewall), where a dead port exits 1.
- Any other command exits **2** on a mismatch — the same code as an overwrite refusal,
  because it is the same kind of event: DVRTool declining to act.

## 4. Deliberate limits

- **URL-only commands do not verify** — deliberately, even when a password is sitting in the
  environment. `dvrtool live-url` and `playback-url` build a string and contact nothing,
  which is the point of them: they work offline, and on a site where RTSP is reachable and
  the web port is not. An identity check there would mean an HTTP round trip, and a timeout
  on it would turn a working command into a failing one. Instead, if the address is already
  pinned, they print what it was last seen as, rather than implying the URL was checked.
  `--expect-serial` on those commands is an error, not a silent no-op.
- **Nothing verifies RTSP or the SDK port's own identity.** A record pins one serial,
  against the port it authenticates on. Pinning all three would mean three ways to be
  half-pinned, and neither of the other two protocols hands out a serial cheaply.
- **A serial is a claim, not an attestation.** Any device that can see the traffic could
  answer with someone else's serial. This is a defence against misconfiguration —
  mistyped ports, re-forwarded NAT rules, records copied and half-edited — not against an
  attacker on the path. That is what TLS plus the certificate pin is for, and the two are
  independent: one proves *what* you are talking to, the other proves *nobody moved in
  between*.
- **A replaced recorder needs one deliberate step.** GUI: **Unbind** in the Edit dialog
  (which also drops the address pin, so the replacement is not refused again from a file
  the operator cannot see). CLI: `--trust-new-device`, or delete the line from
  `identities.json`. There is no automatic "accept the new one" path, because that is
  indistinguishable from the mistake this exists to catch.
- **`--expect-serial` outranks `--trust-new-device`.** They answer different questions, and
  "accept whatever is there now" must not silently cancel "it must be this one".

## 5. What this changed in existing behaviour

- `AccessCard.PanelHost` is now the panel's **port-qualified label**
  (`AccessPanelConnection.Label`), not its bare host. Two panels behind one address used to
  stamp their cards identically, which merged them in the roster and made a fob look
  present on doors it cannot open.
- `--panels` / the Access tab's panel box accept `ip[:port]` per entry; `--port` /
  the **SDK port** box is now the *default* for entries that omit one.
- A panel address listed twice is refused (it would be read twice and every fob on it
  counted twice) — as is `compare --panel X --against X`, which always reported no drift.
- `devices.json` gains `ExpectedSerial`. Records saved before this carry `""` and bind on
  their next successful connection.
