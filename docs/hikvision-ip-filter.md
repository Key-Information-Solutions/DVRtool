# Hikvision IP filter (the recorder's blocklist)

Status: **shipped 2026-09-29**, Hikvision only. Read, add, remove, on/off, in the GUI **IP filter**
tab (`MainWindow.IpFilter.cs`) and `dvrtool ipfilter show | add | remove | enable | disable`.
Live-verified on the lab recorder end to end from both front ends (below).

## Why it exists

Recorders that face the internet get password-guessed. The Illegal Login exception
(`docs/hikvision-exceptions.md`) says *that* it happens; the IP filter is what stops it. It has to be
driven from outside the recorder for two reasons:

- **Most web UIs never show the page.** The first fleet sweep found the filter on 8 of 17 Hikvision
  recorders; only one of them (Site C) was known to have it, because only there does the web UI
  expose it. `isSupportIPFilter` is the honest answer, not the menu.
- **Blocking is repetitive.** The same scanner addresses hit several sites; putting one address on
  every recorder that has a filter is a single `--all-saved` run, not a web session per site.

## The ISAPI surface (read off live firmware, 2026-09-29)

Seen on an M-series NVR (DS-9632NI-M8, V5.04.081 — filter on, 14 blocked) and the lab recorder
(filter off, empty). Both declare the same capabilities.

| Request | What it says |
| --- | --- |
| `GET /ISAPI/System/Network/capabilities` | `isSupportIPFilter` (also `isSupportMACFilter`, not used) — true on the 8 that have it. **On every firmware without a filter the flag is simply absent** and `/ipFilter` answers 403 or 404 (both seen, 2026-09-29); absent flag + no document is "no filter", while a `true` flag with no document is a **failed read**. |
| `GET /ISAPI/System/Network/ipFilter` | The whole filter — see below. |
| `GET /ISAPI/System/Network/ipFilter/capabilities` | `enabled opt="true,false"`, `permissionType opt="deny,allow"`, `IPFilterAddressList size="32"`, `id min=1 max=32`, `addressFilterType opt="mask"`, `ipAddress min=7 max=15`, `ipv6Address min=2 max=39`. |
| `PUT /ISAPI/System/Network/ipFilter` | The whole document back. `ResponseStatus` `statusCode` 1 = OK. |
| `GET /ISAPI/Security/onlineUser` | Sessions logged in **now** (web UI, iVMS, SDK) with `clientAddress/ipAddress`. |
| `GET /ISAPI/System/Network/interfaces` | The recorder's own address and `DefaultGateway`. |

```xml
<IPFilter version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
<enabled>true</enabled>
<permissionType>deny</permissionType>
<IPFilterAddressList size="32">
<IPFilterAddress>
<id>1</id>
<permissionType>deny</permissionType>
<addressFilterType>mask</addressFilterType>
<AddressMask><ipAddress>203.0.113.21</ipAddress></AddressMask>
</IPFilterAddress>
…
</IPFilterAddressList>
</IPFilter>
```

Notes worth keeping:

- **`permissionType` decides what the whole list means.** `deny` is a blocklist; `allow` is an
  allowlist — the listed addresses are the *only* ones let in. Each entry repeats the permission;
  mixed entries have never been seen and are refused rather than interpreted.
- **`addressFilterType` is `mask` and there is no mask field.** One entry is one host. No ranges, no
  CIDR; 32 slots. A list of scanner addresses fills it faster than you would expect.
- **`id` is a slot, not an identity.** DVRTool renumbers 1…n on every write, as the web UI does.
- **The filter drops, it does not refuse.** A blocked host's TCP connects to 80, 554 and 8000 all
  time out (no RST), so from the attacker's side the recorder looks switched off. Web, RTSP and SDK
  ports are all covered by the one list.
- **`onlineUser` does not show DVRTool.** A digest-authenticated ISAPI request opens no session, so
  our own reads never appear there. The list does show the operator's iVMS or web session — which is
  exactly what makes it a useful lockout check.
- **An address added to a filter that is OFF blocks nobody**, and the web UI's list looks the same
  either way. Both front ends say so; `add --enable` / the GUI's "Turn the filter on with Block" do
  both in one write.

## The rules (`IpFilterPlan`, Core, pure)

One piece of arithmetic behind both the CLI dry run and the GUI confirmation.

1. **Blocklists only.** A filter in `allow` mode, with an unreadable mode, or with mixed entries is
   refused **whole** — add, remove and on/off alike. In allow mode any remove, and turning the filter
   on, can lock out everyone, this workstation included, and nothing outside the recorder could undo
   it. The mode is never written; there is deliberately no flag for it.
2. **Never block** — no override: loopback, multicast, reserved, `0.0.0.0`; **the recorder's own
   address and its gateway** (behind a router that masquerades forwarded ports, every remote client
   arrives from the gateway, so blocking it blocks everyone); **this workstation's own interface
   addresses**; and anything named with `--protect` / the GUI's *Never block* box.
3. **This office's public address is not known.** Nothing inside NAT can see it without asking a
   third party, which DVRTool does not do. Name it with `--protect`. (A remote operator logged in
   through iVMS is covered by rule 4 anyway.)
4. **A live session is refused** — "logged into the recorder right now as 'admin' since …". The CLI
   overrides with `--allow-logged-in` for the case where that session *is* the intruder; the GUI has
   no override on purpose.
5. **LAN addresses are refused unless asked** (`--allow-lan`, GUI checkbox): 10/8, 172.16/12,
   192.168/16, 100.64/10, link-local, IPv6 ULA. On a customer site that is usually the site's own
   client.
6. **Strict parsing.** `IPAddress.TryParse` reads `"1"` as `0.0.0.1` and `"10.1"` as `10.0.0.1`,
   which is how a truncated log line becomes a block on somebody else; leading zeros are octal in
   some parsers. Only four decimal octets (or IPv6 where the recorder declares the field) are
   accepted.
7. **Capacity is checked on the result**, and the adds that overflow it are refused by name. A
   recorder that does not state its capacity is refused rather than guessed at.
8. **A plan with any refusal writes nothing** to that recorder — a plan is applied as reviewed or not
   at all. On a fleet run the other recorders still proceed; the refused one says why and the exit
   code is 1.

The write itself goes through the Config tab's guard (`ReReadForWriteAsync`): refused without a
prior read by the same client, refused when the document moved since the read (somebody editing it
from the web UI), and a plan made from any other read is refused too. Afterwards the filter is read
back and compared entry by entry; a recorder that answers `statusCode 1` and keeps its own list is
reported **REJECTED**, never success.

## Front ends

- **CLI** — the automation surface. `--ip` is repeatable and comma-separated; `--from-file` reads one
  address per line with `#` comments, so a log extract can be fed straight in. Dry run by default,
  `--force` to write. `show --all-saved` is the fleet sweep and lists which recorders block which
  address. A wrong device (serial mismatch) aborts the whole run, as everywhere else.
- **GUI** — the IP filter tab. *Read fleet*, then pick one or several recorders (Ctrl/Shift-click).
  The right pane lists the one selected recorder's entries and which other recorders block the same
  address. *Block on selected…*, *Unblock…* (highlighted entries plus anything typed), *Turn on…*,
  *Turn off…*; each re-reads the recorders through the client that will write them, shows every plan
  in a dialog that defaults to **No**, then shows what each recorder holds afterwards.

## Verified live (2026-09-29, the lab recorder)

- Refusals: this workstation, the recorder, a LAN host without `--allow-lan`, `10.1`, a multicast
  address — all refused in one dry run, nothing written.
- CLI: `add --ip <LAN host> --allow-lan --enable --force` → read back "on, blocklist, 1 of 32"; the
  blocked host could no longer connect to 80/554/8000 (timeouts) while this workstation kept reading;
  `remove … --disable --force` → back to "off (blocklist, empty)" and the host connects again.
- GUI: the same round trip — Block (refused first without *Allow LAN*, then confirmed), verified
  blocked from the host, Unblock, Turn off — each read back in the tab.

Not yet fired: any write on a customer recorder, an IPv6 entry, a full (32-entry) list.

## Fleet sweep, 2026-09-29

21 records: 8 Hikvision recorders have the filter, 1 uses it (Site C, 14 addresses), 9 Hikvision
firmwares carry no `isSupportIPFilter` flag and answer 403 or 404 for `/ipFilter`, 4 records are Dahua/Nx (*not implemented*, never "no
filter").
