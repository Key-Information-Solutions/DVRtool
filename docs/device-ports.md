# Device ports — what DVRTool carries, and what it deliberately doesn't

**Established:** 2026-08-24, prompted by the Dahua driver's arrival: the Add-NVR dialog was
built around Hikvision's port layout and quietly applied it to Dahua too.

**Revised:** 2026-08-24, when SDK live view landed. §2 used to say the recorder's SDK port
drove no DVRTool feature at all. That is no longer true on Hikvision — it is now the port
live video comes over when RTSP is closed, which on our fleet is most sites. See
[hikvision-sdk-live.md](hikvision-sdk-live.md).

This doc exists to answer one recurring question — *"there's a port field on the device I
don't see in DVRTool, is that a gap?"* — without having to re-derive the answer each time.
The short version: the SDK port is now vendor-aware, and the two ports that are still
missing (Dahua UDP, Hikvision Enhanced SDK) are missing on purpose, for reasons that are
different from each other.

## 0. TL;DR

- **Dahua's SDK port is 37777, not 8000.** This was a real bug: every Dahua NVR added
  before 2026-08-24 got Hikvision's 8000, and **Test connection** reported a perfectly
  healthy recorder's SDK port as dead. Fixed — `VendorPorts.Sdk(vendor)` in
  `DVRTool.Core`, honoured by the GUI dialog, the CLI's `--sdk-port` default, and a
  one-time correction of saved Dahua entries.
- **Dahua's UDP port (37778) is not needed.** It serves DHNetSDK's datagram login mode and
  broadcast device discovery. DVRTool drives Dahua over HTTP CGI + RTSP and does neither.
- **Hikvision's Enhanced SDK port (8443) is not a missing field — it's a missing
  transport.** Adding the input without the TLS SDK handshake would produce a box that
  accepts a number and changes nothing. See §3 for what it would actually take, and the
  one site condition that would force it.
- **The NVR SDK port is now load-bearing on Hikvision, and still recon on Dahua.** The
  Live tab's SDK transport and `dvrtool live` stream over it — the route that works where
  RTSP is closed. On Dahua nothing dials it, and the port check is exactly what it always
  was: whether SmartPSS / DSS could reach the box from here. Either way it is not the
  Access tab's port — that talks to door panels, on its own address list and its own port
  box.

## 1. The port matrix

| Port | Hikvision / LTS | Dahua / Amcrest | Does DVRTool use it? |
|---|---|---|---|
| HTTP API | 80 | 80 | **Yes** — ISAPI / the CGI tree. Everything depends on it. |
| HTTPS | 443 (our DS-7716NI: **8443**) | 443 | **Yes**, with `--tls` / **Use HTTPS**. Cert pinned trust-on-first-use. |
| RTSP | 554 | 554 | **Yes** — live view, playback-by-time, and the URL commands. |
| Vendor SDK | **8000** — "Server Port" (HCNetSDK) | **37777** — "TCP Port" (DHNetSDK) | **Yes** on Hikvision recorders — live view (§2). **No** on Dahua recorders, where the port check only reports it. **Yes** on door panels (§2). |
| SDK, datagram | — | 37778 — "UDP Port" | **No** — §4. |
| Enhanced SDK | 8443 — SDK over TLS | — | **No** — §3. |

The vendors' own labels are in the table on purpose, and the GUI dialog now uses them: an
installer reading numbers off a recorder's network page should be matching labels, not
translating them.

Note the **8443 collision** in the Hikvision column. 8443 is both the Enhanced SDK service
default *and* where our DS-7716NI was moved to serve HTTPS. They cannot both be on it, so
if the Enhanced SDK is ever implemented it must be a separate field, never inferred from
the HTTPS port.

## 2. Who consumes the recorder's SDK port

`NvrConnection.SdkPort` has three readers now, and they differ by vendor:

| Reader | Vendor | What it does |
|---|---|---|
| `HikvisionSdkSession` | Hikvision | logs in and streams live video over it |
| `ConnectivityProbe.ProbeSdkAsync` | both | TCP connect, for the port check |
| — | Dahua | nothing dials it |

**On Hikvision the number now matters.** `NET_DVR_RealPlay_V40` with `dwLinkMode = 0` brings
the media back over the same session it logged in on, so live view works with nothing but
this port open. On our installed base that is the difference between live view working at 3
sites and at 14, because the SDK port is the one that got forwarded for iVMS-4200. The Live
tab has it as a transport in its dropdown and the CLI has `dvrtool live`; the details, the
channel-numbering trap and the identity story are in
[hikvision-sdk-live.md](hikvision-sdk-live.md).

**On Dahua it is still pure recon.** DVRTool's Dahua driver is HTTP CGI plus RTSP and does
not link `dhnetsdk.dll`, so `CLIENT_RealPlayEx` is not available and the field configures
nothing but the port check. That is worth keeping, because "can SmartPSS / DSS reach this
recorder from where I am standing?" is a question installers genuinely ask and a TCP connect
answers it for free.

**Neither is the Access tab's port.** This is the misreading the section originally existed
to head off, and it still applies: the Access tab and the `access` CLI verbs use a vendor
SDK port, but a *different* one, on different hardware. `AccessPanelConnection` is a
deliberately separate record with its own `SdkPort`.

This section used to end "a DS-K2604 is not in `devices.json` and never will be", and that
changed on 2026-08-26: panels can now be saved (`SavedDevice.Kind = "panel"`), because
retyping the fleet's addresses every session was the real cost. What the old rule was
protecting survives the change — a panel record carries **only** an SDK port (the dialog's
HTTP/RTSP/TLS rows disappear for it), connects with its own credentials, and never feeds a
recorder's `SdkPort` or vice versa. The Access tab's own port box remains, as the default
for *ad-hoc* addresses typed alongside the saved panels (`OCB_SDK_PORT` still prefills it).

The consequence messaging in both front ends is vendor-split to match. A dead SDK port on
Hikvision now reads *"that costs SDK live view — the route that works when RTSP is closed —
and iVMS-4200 / HikCentral cannot reach this recorder from here either"*; on Dahua it still
reads *"no DVRTool feature needs it on Dahua"*. And `dvrtool test` gained the inverse note:
when RTSP is dead but the SDK port answers on a Hikvision unit, it says outright that live
view still works and how.

One thing that did **not** change: `dvrtool test`'s exit code still tracks only the web and
RTSP ports. A script gating a download on it should not fail over a port no download
touches, and SDK live view is not an export.

## 3. Hikvision Enhanced SDK service (8443) — not implemented, and why

Newer Hikvision firmware exposes two SDK listeners under Network → Advanced → More
settings:

- **SDK Service** — port 8000, the legacy plaintext HCNetSDK listener.
- **Enhanced SDK Service** — port 8443, the same protocol inside TLS.

Both of DVRTool's SDK consumers — `HikvisionAccessClient` for door panels and
`HikvisionSdkSession` for live video — log in with a plain `NET_DVR_Login_V30` on the
configured port. Pointing either at 8443 would not work: the enhanced listener expects a TLS
handshake first, so the login would fail with a transport error, not a helpful one. The work
is therefore **not a text box**:

1. Enable TLS in the SDK before login — `NET_DVR_SetSDKLocalCfg` with the TLS local-config
   type — which means new P/Invoke surface in `HcNetSdk.cs` (now in
   `src/DVRTool.Vendors.HikvisionSdk`, shared by both consumers, so this would be done once
   for both).
2. Decide the certificate policy. These panels ship self-signed certs; the HTTP side of
   DVRTool already pins trust-on-first-use into `pins.json`, and the SDK's verification is
   configured separately from `HttpClient`'s, so this would be a second,
   differently-shaped pinning story.
3. Only then add the field, kept distinct from the HTTPS port (see the 8443 collision
   above).
4. Re-verify against the three OCB panels, which is where the honest blocker is: all three
   answer on 8000 today, so there is nothing on site to test an enhanced-port login
   against, and this code path is one that can lock out an IP on repeated failure.

**The condition that would force it:** a hardened site that turns *off* the legacy SDK
service and leaves only the enhanced one. That configuration exists, and on it neither the
Access tab nor SDK live view degrades — both stop working entirely, with a login failure that
looks like bad credentials. If a device is ever reachable on 8443 but not 8000, this section
is the work
item, and it should be done properly rather than by adding an input that silently does
nothing.

## 4. Dahua UDP port (37778) — not needed

Dahua's network page lists TCP 37777 and UDP 37778 side by side, which makes the UDP one
look like half of a pair DVRTool is missing. It isn't:

- 37778 carries DHNetSDK's **datagram login/transport mode** and Dahua's **broadcast
  device discovery** (the "search for devices on the LAN" sweep in SmartPSS/ConfigTool).
- DVRTool's Dahua driver is HTTP CGI plus RTSP. It does not link DHNetSDK at all — there
  is no `DHNetSDK.dll` P/Invoke anywhere in the tree — and it has no discovery feature:
  devices are entered by address.

So there is nothing for the field to configure. A probe would also be close to worthless
even as recon: a UDP "connect" completes locally without a handshake, so an open port and
a firewalled one are indistinguishable without speaking the protocol — the row would pass
on a dead port and teach an installer to ignore it.

If LAN discovery is ever built, that is when 37778 acquires a purpose, and it should arrive
with the feature that uses it.

## 5. Open item: Dahua is still not live-verified

Worth stating alongside the port fixes, because it is the larger Dahua gap. The numbers
here are firmware defaults, and the driver's channel-numbering convention carries its own
`LIVE-TEST ITEM` note at the top of `DahuaClient.cs`: `mediaFileFind`'s
`condition.Channel` base is documented inconsistently by Dahua and Amcrest, and the driver
follows the field-proven 0-based convention rather than the doc's 1-based one.
Doc-conformant firmware would search one channel low. Both that and 37777 want a real
Dahua unit in front of them; the Hikvision paths have hardware behind them and the Dahua
ones do not yet.
