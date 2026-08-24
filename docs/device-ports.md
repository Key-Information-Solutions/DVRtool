# Device ports — what DVRTool carries, and what it deliberately doesn't

**Established:** 2026-08-24, prompted by the Dahua driver's arrival: the Add-NVR dialog was
built around Hikvision's port layout and quietly applied it to Dahua too.

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
- **The NVR SDK port drives no DVRTool feature at all**, either vendor. It is recon: the
  port check reports whether the vendor's own software (iVMS-4200 / SmartPSS) could reach
  the box from here. The GUI now says that outright instead of implying the Access tab
  depends on it — the Access tab talks to door panels, on its own address list and its own
  port box.

## 1. The port matrix

| Port | Hikvision / LTS | Dahua / Amcrest | Does DVRTool use it? |
|---|---|---|---|
| HTTP API | 80 | 80 | **Yes** — ISAPI / the CGI tree. Everything depends on it. |
| HTTPS | 443 (our DS-7716NI: **8443**) | 443 | **Yes**, with `--tls` / **Use HTTPS**. Cert pinned trust-on-first-use. |
| RTSP | 554 | 554 | **Yes** — live view, playback-by-time, and the URL commands. |
| Vendor SDK | **8000** — "Server Port" (HCNetSDK) | **37777** — "TCP Port" (DHNetSDK) | **No** on recorders — reported by the port check only. **Yes** on door panels (§2). |
| SDK, datagram | — | 37778 — "UDP Port" | **No** — §4. |
| Enhanced SDK | 8443 — SDK over TLS | — | **No** — §3. |

The vendors' own labels are in the table on purpose, and the GUI dialog now uses them: an
installer reading numbers off a recorder's network page should be matching labels, not
translating them.

Note the **8443 collision** in the Hikvision column. 8443 is both the Enhanced SDK service
default *and* where our DS-7716NI was moved to serve HTTPS. They cannot both be on it, so
if the Enhanced SDK is ever implemented it must be a separate field, never inferred from
the HTTPS port.

## 2. Why the recorder's SDK port has no consumer

`NvrConnection.SdkPort` is read by exactly one caller: `ConnectivityProbe.ProbeSdkAsync`.
Nothing else in the codebase dials it.

This is easy to misread as a wiring bug, so, explicitly: the Access tab and the `access`
CLI verbs *do* use a vendor SDK port, but a different one. Door panels are separate
hardware with separate addresses — `AccessPanelConnection` is a deliberately separate
record with its own `SdkPort`, fed by the Access tab's own port box (`OCB_SDK_PORT`), not
by anything in the NVR list. A DS-K2604 is not in `devices.json` and never will be.

The field stays anyway, because "can iVMS-4200 / SmartPSS reach this recorder from where I
am standing?" is a question installers genuinely ask, and a TCP connect answers it for
free alongside the two ports that do matter. What changed is that the UI no longer
overstates it: the hint under the box names the software that needs the port, and a failed
SDK row now says *"no DVRTool feature needs it, so this only means SmartPSS / DSS cannot
reach this recorder from here"* rather than the old, and wrong, *"the Access tab needs
it"*.

## 3. Hikvision Enhanced SDK service (8443) — not implemented, and why

Newer Hikvision firmware exposes two SDK listeners under Network → Advanced → More
settings:

- **SDK Service** — port 8000, the legacy plaintext HCNetSDK listener.
- **Enhanced SDK Service** — port 8443, the same protocol inside TLS.

`HikvisionAccessClient` logs in with a plain `NET_DVR_Login_V30` on the configured port
(`src/DVRTool.Vendors.HikvisionAccess/HikvisionAccessClient.cs`). Pointing that at 8443
would not work: the enhanced listener expects a TLS handshake first, so the login would
fail with a transport error, not a helpful one. The work is therefore **not a text box**:

1. Enable TLS in the SDK before login — `NET_DVR_SetSDKLocalCfg` with the TLS local-config
   type — which means new P/Invoke surface in `HcNetSdk.cs`.
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
service and leaves only the enhanced one. That configuration exists, and on it the Access
tab does not degrade — it stops working entirely, with a login failure that looks like bad
credentials. If a panel is ever reachable on 8443 but not 8000, this section is the work
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
