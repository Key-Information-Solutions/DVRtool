# Hikvision access control — verified protocol reference

**Established:** 2026-08-19, live against Site A's three OCB (Hikvision DS-K2604)
panels, from the relay host.
**Supersedes the guesses in** `hikvision-access-control-handoff.md` — read that for the
mission and the safety rules, this for what the hardware actually does.

Everything below was confirmed on the wire, not read off a datasheet. The authoritative
struct/command reference is Hikvision's own SDK documentation, which is publicly readable
at `https://open.hikvision.com/hardware/structures/<STRUCT_NAME>.html` and
`.../definitions/<FUNCTION>_ACS.html` (GB2312-encoded; reachable from the relay host, not from
the the dev workstation network).

## 1. Transport

SDK only, port 8000. The handoff's §5 conclusion holds: no HTTP on these panels, and
`NET_DVR_STDXMLConfig` answers **every** ISAPI URL with error 23 (`NOSUPPORT`). There is no
ISAPI path on DS-K2604 V2.0 firmware.

## 2. Commands that matter

| Constant | Value | Interface | In | Out |
|---|---|---|---|---|
| `NET_DVR_GET_CARD_CFG_V50` | **2178** | `NET_DVR_StartRemoteConfig` | `NET_DVR_CARD_CFG_COND` | `NET_DVR_CARD_CFG_V50` per card, via callback |
| `NET_DVR_SET_CARD_CFG_V50` | **2179** | `NET_DVR_StartRemoteConfig` + `NET_DVR_SendRemoteConfig` | `NET_DVR_CARD_CFG_COND` | status via callback |
| `NET_DVR_GET_CARD_CFG` | 2116 | (legacy — use the V50 command) | | |
| `NET_DVR_SET_CARD_CFG` | 2117 | (legacy) | | |
| `NET_DVR_GET_CARD_USERINFO_CFG` | **2163** | `NET_DVR_GetDeviceConfig` | `NET_DVR_CARD_CFG_SEND_DATA` | `NET_DVR_CARD_USER_INFO_CFG` |
| `NET_DVR_SET_CARD_USERINFO_CFG` | 2164 | `NET_DVR_SetDeviceConfig` | | |
| `ENUM_ACS_SEND_DATA` (`dwDataType`) | **0x3** | — | — | — |

**Correction to handoff §7:** enumeration is *not* a `NET_DVR_GetNextRemoteConfig` polling
loop. Records arrive on the **callback** passed to `NET_DVR_StartRemoteConfig`, terminated by
a status callback. Callback contract:

- `dwType`: 0 = status, 1 = progress (unused here), 2 = data.
- status values: `SUCCESS = 1000`, `PROCESSING = 1001`, `FAILED = 1002`, `EXCEPTION = 1003`.
- a `FAILED` buffer is `4-byte status + 4-byte error code + 32-byte card number`.
- The callback runs on an SDK-owned **native thread**. A PowerShell scriptblock delegate
  crashes the process outright there; the delegate must be built in C# and rooted.

Fetching **all** cards needs no `SendRemoteConfig` — set `dwCardNum = 0xFFFFFFFF` in the
condition and just collect callbacks. A **targeted** lookup sets `dwCardNum = 1` and sends a
`NET_DVR_CARD_CFG_SEND_DATA`. Both are verified working.

## 3. Struct layouts (byte-verified)

`NET_DVR_CARD_CFG_COND` = 40 bytes. `NET_DVR_CARD_CFG_SEND_DATA` = 52.
`NET_DVR_CARD_USER_INFO_CFG` = 292. `NET_DVR_TIME_EX` = 8, `NET_DVR_VALID_PERIOD_CFG` = 52.

`NET_DVR_CARD_CFG_V50` = **2708 bytes**, and the device stamps that into `dwSize`, which is a
free self-check on every read. Offsets the driver relies on:

| Offset | Field |
|---|---|
| 0 | `dwSize` (2708) |
| 4 | `dwModifyParamType` |
| 8 | `byCardNo[32]` (ASCII) |
| 40 / 41 / 42 / 43 | `byCardValid` / `byCardType` / `byLeaderCard` / `byUserType` |
| 44 | `byDoorRight[256]` — one byte per door, 1-based |
| 300 | `struValid` (`byEnable`, begin/end flags, then two `NET_DVR_TIME_EX`) |
| 352 | `byBelongGroup[128]` |
| 480 | `byCardPassword[8]` |
| 488 | `wCardRightPlan[256][4]` (WORD) |
| 2536 / 2540 | `dwMaxSwipeTime` / `dwSwipeTime` |
| 2548 | `dwEmployeeNo` |
| 2552 | `byName[32]` (GB2312) |
| 2584 | `wDepartmentNo` |
| 2612 / 2616 / 2620 | `dwCardRight` / `dwPlanTemplate` / `dwCardUserId` |
| 2624 | `byCardModelType` |

Three real records are checked in at `tests/DVRTool.Tests/Fixtures/card-records.b64` and the
codec is tested against them.

### Two traps in the write path

1. **`dwModifyParamType` gates everything.** A set command with it left at 0 changes
   *nothing* — every field reads as "leave alone". Each field written needs its bit
   (`CARD_PARAM_CARD_VALID = 0x1`, `..._VALID = 0x2` (period), `..._CARD_TYPE = 0x4`,
   `..._DOOR_RIGHT = 0x8`, `..._LEADER_CARD = 0x10`, `..._RIGHT_PLAN = 0x100`,
   `..._EMPLOYEE_NO = 0x400`, `..._NAME = 0x800`, `CARD_USER_TYPE = 0x40000`).
2. **Door rights and right plans are two halves of one permission.** Every live card carries
   `wCardRightPlan[door][0] = 1` for each door in `byDoorRight`. Grant a door but leave the
   plan at 0 and the card holds the right with no schedule attached — it lists correctly and
   never opens the door. `CardRecord.SetDoors` writes both together for this reason.

**Deleting a card** is `byCardValid = 0` with the `CARD_PARAM_CARD_VALID` bit. There is no
separate delete verb.

## 4. The finding that changes the mission: these panels store no cardholder identity

Answering handoff §9.2 ("person vs card model"): it is the **card** model, and thinner than
even that implies.

Across **all 170 credentials on all three panels**, every single record has
`byName` empty, `dwEmployeeNo == 0`, and `dwCardUserId == 0`. The card→name channel
(`NET_DVR_GET_CARD_USERINFO_CFG`, 2163) answers **error 23 NOSUPPORT on all three panels** —
including .222, which runs the newer V2.0.009 firmware. The SDK capability query
(`NET_DVR_GetDeviceAbility` / `ACS_ABILITY` = 0x801) is refused as well.

So a panel credential is exactly: **a fob number + which doors it opens + a validity window.**
There is no person, no name, no employee number.

**Consequence:** `dvrtool access find --name "First.Last"` cannot be answered from the panels,
because the panels do not know anyone's name. Names exist only in iVMS-4200's own database.
The offboarding question "does this departed employee still have door access?" is therefore
only answerable via the fob number. The CLI reports this explicitly rather than returning an
empty result that would read as "no access found" — see `AccessRoster.AnyPanelStoresNames`.

`byName` is a writable field in the struct the panels accept, so writing names *into* the
panels looked like a way to make DVRTool self-sufficient. **It does not work.** The canary
write (§5a) set `byName` to `Test.Canary` with the `CARD_PARAM_NAME` bit set; the panel
accepted the write, created the card correctly, and **silently discarded the name** — it reads
back empty. Consistent with Hikvision's own note that `byName` is required only for
attendance-terminal models (DS-K1T803F, DS-K1A801F) and optional elsewhere: on a DS-K2604
there is simply nowhere to put it.

So the person↔fob mapping **cannot** live on these panels. It has to live either in iVMS or
in DVRTool.

## 5. Live fleet shape (2026-08-19)

| Panel | Serial | Firmware | Fobs |
|---|---|---|---|
| 192.0.2.221 | `OCB-K260420180706V020004ENC00000001` | V2.0.4 | 149 |
| 192.0.2.222 | `OCB-K260420211028V020009ENC00000002` | V2.0.9 | 15 |
| 192.0.2.223 | `OCB-K260420180706V020004ENC00000003` | V2.0.4 | 6 |

Answering handoff §9.3 ("uniform or per-door?"): **per-door.** .221 holds the full roster and
.222/.223 are strict subsets of it — 134 fobs exist only on .221. Door patterns across the
170 presence rows: door 1 only ×141, all four ×18, 1+4 ×4, door 2 only ×4, 1+2 ×3. An
offboarding check must therefore query all three panels regardless.

Regenerate this with `dvrtool access export`. The CSV is a list of credentials that open a
customer's doors, so `artifacts/` is gitignored and a roster must never be committed.

Serials are the only self-description these panels offer (`NET_DVR_DEVICEINFO_V30` carries no
model string for access controllers), so model/firmware/door-count are parsed from them —
see `PanelIdentity`. iVMS renders the firmware zero-padded ("V2.0.004") where we render
"V2.0.4"; the raw serial is always reported alongside.

## 5a. Write path — verified live (2026-08-19)

A canary round trip was run against .223 (the quietest panel, 6 fobs) on an unused fob
number, with operator approval:

```
access grant  --card 9001 --doors 1 --panel 192.0.2.223 --name Test.Canary --force
access find   --card 9001
access revoke --card 9001 --force
```

Results:

- **The write works.** `NET_DVR_SET_CARD_CFG_V50` (2179) created fob 9001 with door 1, and the
  read-back confirmed it.
- **Read-modify-write is safe.** A full before/after listing of .223 differed by exactly the
  one added line; the six existing fobs were untouched.
- **`byName` is discarded** (see §4).
- **Revoke deletes rather than deactivates.** After `byCardValid = 0` the record is *gone*
  from the enumeration entirely, not present-and-invalid. So `FullyRevoked` describes a state
  these panels don't actually keep — it's there for firmware/vendors that do.
- **After the revoke, .223 was byte-identical to its pre-canary listing.** Clean rollback.
- A card written with no validity period is created active and **never expires**. Every fob
  iVMS provisioned here carries a ~10-year window, so `grant` prints a notice when no
  `--valid-until` is given.

## 6. Deployment

the relay host has .NET 5/6/8 but not 10, so the CLI must be published self-contained:

```bash
dotnet publish src/DVRTool.Cli/DVRTool.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

The SDK itself is found automatically (HikCentral Lite / iVMS-4200 install paths), or set
`OCB_SDK_DIR`. Only `HCCore.dll` and `HCNetSDKCom\HCCoreDevCfg.dll` are pre-loaded on
purpose: loading the whole plugin folder pulls in the audio plugins, whose initializers print
`Load OpenAL32.dll success!` to **stdout** and corrupt the tool's own output.

## 7. Still open for the operator

1. **Where does person↔fob live?** Not on the panels — §4 settles that. Either import the
   mapping out of iVMS-4200 once and keep it in DVRTool, or stay fob-number-only and let iVMS
   remain the name authority. Until this is decided DVRTool is the authority for
   *fob → doors* across the fleet, but *person → fob* still requires iVMS.
2. **Authority vs iVMS.** Unchanged from handoff §4.4: iVMS's "Get from Device" pulls a panel
   *into* iVMS, so an SDK write lands on the panel and iVMS won't know until someone
   re-syncs. Decide direction before routine writes.
