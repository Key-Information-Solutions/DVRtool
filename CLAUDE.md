**MANDATORY:** Workflows are allowed, but they MUST never use Fable unless asked. Opus should be used for implementing, Sonnet used for big research, and Haiku used for small tasks or local searching. Individual agents do not abide by these restrictions. ALWAYS commit changes, even to main, assuming they build and pass tests. Branches and worktrees should merge to main after commit.

**Positioning:** DVRTool is a GUI-first Windows desktop app (`src/DVRTool.App`, WPF) with a `dvrtool`
CLI (`src/DVRTool.Cli`) as its automation/scripting surface; both front ends ride the shared
`src/DVRTool.Core` engine, so export/remux/overwrite and name-enrichment behavior is identical between them.

**Device ports:** The three ports an NVR record carries are per-vendor, not universal — the
vendor SDK port is 8000 on Hikvision and **37777** on Dahua (`VendorPorts.Sdk` in
`DVRTool.Core`, honoured by the GUI Add-NVR dialog, `dvrtool --sdk-port` and `dvrtool test`).
Read `docs/device-ports.md` before adding or changing a port field: it records why Dahua's UDP
port and Hikvision's Enhanced SDK port are deliberately absent, and that the recorder's SDK
port drives no DVRTool feature at all (the Access tab's port is a separate field for separate
hardware).

**Access control:** Hikvision/OEM door panels are surfaced primarily in the GUI Access tab
(`src/DVRTool.App`, `MainWindow.Access.cs`) for viewing rosters and importing cardholder names; the
`access` CLI command group provides the same reads **plus** the gated writes (`grant`/`revoke`) for
automation. Both go through `src/DVRTool.Vendors.HikvisionAccess` (HCNetSDK P/Invoke over the
SDK port, 8000 by default). Reads and writes are both live-verified against Site A's
three OCB panels (writes via an approved canary round trip on a throwaway fob, rolled back
clean; the GUI tab stays read-only toward the panels).
Read `docs/hikvision-access-control-findings.md` before touching it — notably: these panels store **no
cardholder names**, and `dwModifyParamType` plus the door/right-plan pairing are the two traps that
silently produce a card that never opens a door.

**Name enrichment:** Because the panels hold no names, cardholder names are imported **one-way** from
iVMS/NVMS. The primary surface is the GUI Access tab's "Cardholder names (from iVMS)" group (Import CSV /
Import from iVMS database / Cache key / Clear map); the `access identity` verbs are the CLI/automation
equivalent (`src/DVRTool.Vendors.HikvisionIvms`): `--import-csv` (the supported plaintext Person export —
complete path), `--import-ivms` (reads the live SQLCipher DB and correlates names to fobs by unique expiry
— partial), `--capture-key`/`--where`/`--clear`. The map is cached in a DVRTool-owned file and applied
automatically to the roster/find/export views in both front ends. The per-install SQLCipher key is supplied
by the operator at runtime (flag / `IVMS_DB_KEY` / cached key file) — **never hardcoded**; no key capture
(debugger) or write-back into iVMS is implemented, and the `Card.CardNo` cipher is deliberately not used.
Read `docs/ivms-integration-findings.md` before touching it.
