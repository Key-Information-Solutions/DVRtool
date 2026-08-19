# Handoff — Access-Control support for DVRTool (Hikvision/OEM "OCB" door panels)

**Written:** 2026-08-19, by a session working from the Site A Seating Charts project.
**For:** a session assigned to `E:\DVRtool` that will implement this against the **live** Site A door-access system.
**Status (updated 2026-08-19):** IMPLEMENTED for reads; writes are coded but not yet fired at
production. See **`hikvision-access-control-findings.md`** for what the hardware actually does —
it corrects §7 (enumeration is callback-driven, not a `GetNextRemoteConfig` loop), answers the
§9.2/§9.3 open questions, and records the finding that reshapes the mission: **these panels store
no cardholder names at all**, so the name-based offboarding lookup in §1 is not answerable from
the panels. §4's safety rules and §3's access details below remain accurate and in force.

---

## 1. Mission

Add door-access-control support to DVRTool so onboarding/offboarding can be automated:

- **Read (build first):** enumerate the persons and their fobs (cards) provisioned on each door panel; look a person up by name. This closes the last manual step in Site A's offboarding — verifying a departed employee's door access is actually gone.
- **Write (build second, gated):** create a fob for a new hire, revoke/delete a person or fob for a leaver.

This is a **KIS-general tool feature** (DVRTool is not customer-scoped), but the only live system to build against right now is **Site A**. Everything you need to reach it is below.

## 2. Why this belongs in DVRTool

DVRTool already talks to Hikvision gear and already has the right seams:

- `INvrClient` (video) and `IUserManagementClient` (device accounts) are **separate opt-in interfaces** in `DVRTool.Core` — precisely because "not every device exposes X." Access control is the next sibling: **`IAccessControlClient`**.
- The existing Hikvision **video** driver (`DVRTool.Vendors.Hikvision`) speaks **ISAPI over HTTP+digest**. The door panels **cannot** be driven that way (see §5). So access control is a **new vendor project with a different transport** — the Hikvision **HCNetSDK** (`NET_DVR_*`) over port 8000 via P/Invoke.
- The repo's safety ethos (refuse-to-overwrite, atomic promote, `--force` as explicit consent in `AtomicDownload`) maps directly onto access-control **writes**, which are higher-stakes than a file overwrite.

## 3. The target system (LIVE PRODUCTION — read §4 before touching it)

| Panel IP | Model | Notes |
|---|---|---|
| `192.0.2.221` | Hikvision **DS-K2604** (OEM-badged "OCB"), 4-door | firmware V2.0.004 (2018 unit) |
| `192.0.2.222` | DS-K2604 / "OCB" | firmware V2.0.009 (2021 unit) |
| `192.0.2.223` | DS-K2604 / "OCB" | firmware V2.0.004 (2018 unit) |

- **"OCB" is the OEM brand** — it's literally the serial prefix (`OCB-K2604...`). `devType = 850`. These are the classic 4-door controllers the blue key-fobs terminate on. There is **also** an NVR at `192.0.2.17` (`admin`/`REDACTED-ROTATE-THIS-PASSWORD`) and cameras — **those are video, not access control; ignore them for this work.**
- **Transport:** SDK **port 8000 only**. The panels have **no HTTP/HTTPS** and Remote Configuration exposes no way to enable it. Confirmed closed: 80, 443, 81, 88, 8080, 8081, 8443, 7071 on all three. 8000 is open on all three.
- **Credentials:** username `admin`, password in `.env` as `OCB_PASS` (also `OCB_USER`, `OCB_PANELS`, `OCB_SDK_PORT`). **Verified working on all three panels** via `NET_DVR_Login_V30` and `NET_DVR_Login_V40`. The panel password is **different** from the NVR/camera password — do not confuse them.

### How to reach the panels (you are not on their network)

The panels live on Site A's LAN (`192.0.2.0/24`). Your repo is on **the dev workstation**, which has **no route** to that subnet. All live interaction goes through **the relay host** (`DOMAIN\svc-account`, elevated), which is on that LAN and has the Hikvision SDK installed.

- **Interactive probing / fast iteration:** the `remote-agent` MCP — `mcp__remote-agent__remote_exec` with `host="the relay host"`, `shell="powershell"`. This is how all the recon in §6 was done: PowerShell + `Add-Type` P/Invoke against the SDK DLLs already on the relay host. Iterate struct layouts here **before** writing C# — it's a seconds-long loop with no build/deploy.
- **Deploying the built CLI to run live:** `dotnet publish -r win-x64 --self-contained` (the relay host may not have your target .NET runtime), then push via the remote-agent relay:
  ```bash
  curl.exe -sS --fail-with-body --url-query "host=the relay host" --url-query "path=C:\Temp\dvrtool\dvrtool.exe" --url-query "overwrite=1" -H "Authorization: Bearer $env:REMOTE_AGENT_MCP_TOKEN" -T ".\publish\dvrtool.exe" http://203.0.113.10:8766/api/push
  ```
  The exe must run where HCNetSDK's dependencies resolve — see §7 on the working-directory / DLL requirement.

## 4. Hard safety constraints — read before any live call

1. **Login lockout is shared with iVMS.** The panels lock out a *source IP* after repeated failed logins (~5 failures → ~30 min lock). iVMS-4200 (on the relay host) logs into these panels from the **same IP** you will. **A wrong-password guessing loop from the relay host can lock iVMS out of its own door panels.** The password is known and in `.env`, so you should never be guessing — but if you ever test credentials, it's **one attempt, stop on success.**
2. **Always `NET_DVR_Logout` then `NET_DVR_Cleanup`.** Set `NET_DVR_SetReconnect(_, 0)` so the SDK doesn't spin background reconnect threads.
3. **Read before write, always.** The read path (§ enumeration) has zero side effects. Prove it fully before writing anything.
4. **iVMS divergence — the real trap.** iVMS-4200 keeps a *central* Person roster and treats each panel as its source of truth. Its **"Get from Device"** button pulls a panel's records **into** iVMS and **overwrites** iVMS's copy (it even prompts you to export the config first). So: an SDK write you make lands on the **panel**, and iVMS won't know until someone re-syncs — and a re-sync in the wrong direction could clobber your change or resurrect a deleted one. Decide authority *before* shipping writes (see §9). For the read phase this doesn't matter.
5. **These are physical doors.** A botched write can lock staff out or grant a stranger access. Writes must be tested on a **throwaway test person/fob first**, verified present, then verified removed — never a live employee as the first canary. Mirror the repo's `--force`-gated, verify-after-write discipline.
6. **This is customer production infrastructure.** Even though DVRTool is KIS-general, you are operating Site A's live access-control system. Be conservative.

## 5. Course-correction: it is NOT ISAPI (don't waste time here)

An earlier plan assumed `NET_DVR_STDXMLConfig` would tunnel ISAPI JSON (`/ISAPI/AccessControl/UserInfo/Search` etc.) over port 8000. **It does not on these panels.** `NET_DVR_STDXMLConfig` returns **error 23 = `NET_DVR_NOSUPPORT`** for *every* URL, including a bare `GET /ISAPI/System/deviceInfo`. (Caution: this SDK build's `NET_DVR_GetErrorMsg` has no string for 23 and misreports it as "No error" — don't trust that. 23 is NOSUPPORT.) DS-K2604 at V2.0 firmware simply doesn't implement the ISAPI-over-SDK bridge. **Do not build the driver around ISAPI/JSON.**

## 6. What's already proven (saves you the recon)

All of the following was run live against the panels and confirmed:

- **SDK loads and initializes** from 64-bit PowerShell using the 64-bit SDK at `C:\Program Files (x86)\HikCentral Lite\Client\HCNetSDK.dll` (v6.1.9.139). The plugin folder `HCNetSDKCom\` (incl. `HCCoreDevCfg.dll`) and `HCCore.dll`, `libcrypto-3.dll` are all present there. (There's also a 32-bit set under the iVMS-4200 install — only if you ever go 32-bit.)
- **Login works** via both `NET_DVR_Login_V30` and `NET_DVR_Login_V40` on all three panels with `admin`/`OCB_PASS`.
- **STDXMLConfig = NOSUPPORT** (see §5).
- **The structured device-config channel is ALIVE.** `NET_DVR_GetDeviceConfig` with `NET_DVR_GET_ACS_WORK_STATUS_V50` (command **2110**) returned error **17 = `NET_DVR_PARAMETER_ERROR`** (my input buffer was wrong), **not** 23 — i.e. the ACS config subsystem responds; only the exact struct/params need to be right. **This is the path.**

### Proven login harness (paste-ready starting point for probes)

Run via `remote_exec` on the relay host. This is the exact shape that worked — note the caveats baked in (null-terminated request URLs, `CharSet.Ansi` strings, 64-bit process + 64-bit SDK, `SetDllDirectory` + `CurrentDirectory` so dependent DLLs/plugins resolve):

```powershell
$sdk='C:\Program Files (x86)\HikCentral Lite\Client'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Hik {
  [DllImport("kernel32", CharSet=CharSet.Unicode)] public static extern IntPtr LoadLibrary(string p);
  [DllImport("kernel32", CharSet=CharSet.Unicode)] public static extern bool SetDllDirectory(string p);
  [DllImport("HCNetSDK.dll")] public static extern bool NET_DVR_Init();
  [DllImport("HCNetSDK.dll")] public static extern bool NET_DVR_Cleanup();
  [DllImport("HCNetSDK.dll")] public static extern uint NET_DVR_GetLastError();
  [DllImport("HCNetSDK.dll")] public static extern bool NET_DVR_SetConnectTime(uint wait, uint tries);
  [DllImport("HCNetSDK.dll")] public static extern bool NET_DVR_SetReconnect(uint interval, int enable);
  [DllImport("HCNetSDK.dll", CharSet=CharSet.Ansi)] public static extern int NET_DVR_Login_V30(string ip, ushort port, string user, string pass, IntPtr devInfo);
  [DllImport("HCNetSDK.dll")] public static extern bool NET_DVR_Logout(int userId);
  [DllImport("HCNetSDK.dll")] public static extern bool NET_DVR_GetDeviceConfig(int userId, uint cmd, uint count, IntPtr inBuf, uint inSize, IntPtr statusList, IntPtr outBuf, uint outSize);
}
"@
[Environment]::CurrentDirectory=$sdk
[void][Hik]::SetDllDirectory($sdk)
[void][Hik]::LoadLibrary((Join-Path $sdk 'HCNetSDK.dll'))
[void][Hik]::NET_DVR_Init()
[void][Hik]::NET_DVR_SetConnectTime(2500,1)   # 2.5s connect, 1 try
[void][Hik]::NET_DVR_SetReconnect(5000,0)     # no background reconnect
$M=[Runtime.InteropServices.Marshal]
$dev=$M::AllocHGlobal(512)
$uid=[Hik]::NET_DVR_Login_V30('192.0.2.221',8000,'admin',$env:OCB_PASS,$dev)  # or pass the literal
# ... structured ACS calls here ...
if($uid -ge 0){ [void][Hik]::NET_DVR_Logout($uid) }
$M::FreeHGlobal($dev)
[void][Hik]::NET_DVR_Cleanup()
```

Device serial (model proof) = first 48 bytes of the `NET_DVR_DEVICEINFO_V30` buffer as ASCII, e.g. `OCB-K260420180706V020004ENC...`.

## 7. The SDK you must use (Hik Device Network SDK — Access Control)

The driver's transport is **HCNetSDK P/Invoke over 8000**, using the **structured access-control APIs** (NOT ISAPI). The enumeration pattern for DS-K panels is the async remote-config loop:

```
NET_DVR_StartRemoteConfig(userId, <GET command>, &searchCond, sizeof(cond), cb, pUser)  -> handle
loop: NET_DVR_GetNextRemoteConfig(handle, &record, sizeof(record))
      -> NET_SDK_GET_NEXT_STATUS_SUCCESS (use record) / _NEED_WAIT (retry) / _FINISH (done) / _FAILED
NET_DVR_StopRemoteConfig(handle)
```

Writes use `NET_DVR_SetDeviceConfig` (or the corresponding remote-config SET command) with the person/card struct.

**You must confirm the exact command constants and struct versions** — they vary by SDK/firmware and this is where the real work is. Two ways, use both:

1. **Authoritative:** the **"Device Network SDK (for Access Control)" developer guide + headers** from Hikvision/the OEM. This gives you `NET_DVR_GET_USERINFO_CFG` / `NET_DVR_SET_USERINFO_CFG`, `NET_DVR_GET_CARD_CFG` / `NET_DVR_SET_CARD_CFG` (and the `_V50` variants), their search-condition and record structs, and the command numeric IDs. **Note:** the relay host has only the runtime DLLs, **not** the headers — download the SDK package. Anchor you already have: `NET_DVR_GET_ACS_WORK_STATUS_V50 = 2110`.
2. **Empirical:** nail struct sizes/layouts the same way login was nailed — `remote_exec` P/Invoke probes, watching for err 17 (param/struct wrong) vs a clean return. `NET_DVR_GetLastError` + trial on the live panel converges fast.

**Person vs card model caveat:** DS-K2604 at V2.0 firmware may use the older **card + user** model rather than the newer **"person"** model. Determine which these panels speak early — it changes the structs. Join key for offboarding is the **person name** (Site A convention is `First.Last`); a **fob = a card number** attached to a person/user.

## 8. Architecture (match the existing repo patterns)

- **`DVRTool.Core`**
  - `IAccessControlClient` — sibling to `IUserManagementClient`. Suggested surface:
    ```csharp
    Task<IReadOnlyList<AccessPerson>> GetPersonsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AccessCard>> GetCardsAsync(string personId, CancellationToken ct = default);
    Task<AccessPerson?> FindPersonByNameAsync(string name, CancellationToken ct = default);   // offboarding
    // writes (phase 2, gated):
    Task AddCardAsync(string personId, string cardNo, CancellationToken ct = default);
    Task DeletePersonAsync(string personId, CancellationToken ct = default);
    Task RevokeCardAsync(string cardNo, CancellationToken ct = default);
    ```
  - Models: `AccessPerson(Id, Name, EmployeeNo, ...)`, `AccessCard(CardNo, PersonId, Active, ...)` — records, like the existing `NvrUser`/`Channel`.
- **`DVRTool.Vendors.HikvisionAccess`** (new project) — the SDK driver.
  - Isolate all P/Invoke in one class (e.g. `HcNetSdk.cs`): `DllImport`s, structs, the login/logout/cleanup lifecycle, the remote-config enumeration helper. The public `HikvisionAccessClient : IAccessControlClient` stays clean.
  - **Transport is deliberately different** from the video `HikvisionClient` (which uses `NvrHttp`/digest). Do **not** try to share `NvrHttp` here.
  - Marshalling gotchas already learned: 64-bit process ↔ 64-bit SDK; strings `CharSet.Ansi`; set process working dir to the SDK folder (or ship the SDK runtime set beside the exe) so `HCCore.dll` + `HCNetSDKCom\` plugins load; request buffers null-terminated.
- **`DVRTool.Cli`** — add an `access` command group, consistent with existing verbs (`info`, `channels`, `users`, ...). Reads `OCB_*` from `.env` like the video side reads `DVR_*`. Proposed:
  ```
  dvrtool access persons   --panel <ip|all>
  dvrtool access find       --name "First.Last" --panel all
  dvrtool access cards      --person <id> --panel <ip>
  dvrtool access add-card   --person <id> --card <no> --panel <ip>   # gated, --force style
  dvrtool access revoke     --card <no> --panel <ip>
  ```
- **`tests/DVRTool.Tests`** — unit-test the driver against canned SDK record buffers (mirror `MockHttpHandler` approach: feed captured byte layouts, assert parsed models). The SDK itself can't be mocked, so test the parsing/marshalling layer with real captured bytes.
- **Offboarding integration:** the Site A offboarding tool (`offboard_user.ps1`, separate repo on the relay host) currently just prints a manual "delete from iVMS" checklist line. Once `dvrtool access find --name --panel all` works, that tool can shell out to it. Coordinate with the operator; don't wire it blindly.

## 9. Open decisions for the operator (Josh) — surface these, don't guess

1. **Authoritative store:** panel-direct + iVMS "Get from Device" resync, or keep iVMS authoritative and only *read* via SDK? (Affects whether writes ship at all — see §4.4.)
2. **Person vs card model** on this firmware — confirm before designing the write structs.
3. **Which panels a given person should exist on** — is access uniform across all 3 doors, or per-door? A thorough offboarding check queries all three regardless.

## 10. Definition of done

- **Read:** `dvrtool access persons --panel all` returns the live roster from all three panels; a person count that reconciles with what iVMS-4200's Person module shows. `dvrtool access find --name "First.Last"` returns hits across panels. Zero writes, zero config change.
- **Write (later):** create a **test** fob on a **test** person, verify it reads back, revoke it, verify it's gone — with a rollback path — before any real employee is touched.
- **Safety:** no failed-login loops (lockout rule honored); every session logs out + cleans up; writes are `--force`-gated and verified after the fact.

## 11. Appendix — quick facts

- SDK dir (64-bit): `C:\Program Files (x86)\HikCentral Lite\Client` — `HCNetSDK.dll` v6.1.9.139, `HCNetSDKCom\HCCoreDevCfg.dll` present.
- SDK dir (32-bit, fallback): `C:\Program Files (x86)\iVMS-4200 Site\iVMS-4200 Client\Client`.
- the relay host agent identity: `DOMAIN\svc-account` (elevated). remote-agent relay server: `http://203.0.113.10:8766` (token in `$env:REMOTE_AGENT_MCP_TOKEN`).
- iVMS-4200 Site is the incumbent GUI managing these panels (on the relay host). An unpassworded iVMS **config export** exists at `a local backup file on that host` — it is a rollback artifact, **not** a credential source (its device DB is encrypted; the panel passwords are not extractable from it).
- Error codes seen: `1` = password wrong, `7` = connect failed, `17` = parameter/struct error (channel alive, keep going), `23` = NOSUPPORT (function absent — stop trying it).
