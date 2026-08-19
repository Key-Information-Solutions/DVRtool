**MANDATORY:** Workflows and agents are allowed, but they MUST never be Fable. Opus should be used for implementing, Sonnet used for big research, and Haiku used for small tasks or local searching.

**Access control:** Hikvision/OEM door panels are supported via the `access` CLI command group
(`src/DVRTool.Vendors.HikvisionAccess`, HCNetSDK P/Invoke over port 8000). Reads and writes are both
live-verified against Site A's three OCB panels (writes via an approved canary round trip on a
throwaway fob, rolled back clean). Read `docs/hikvision-access-control-findings.md` before touching it —
notably: these panels store **no cardholder names**, and `dwModifyParamType` plus the door/right-plan
pairing are the two traps that silently produce a card that never opens a door.

**Name enrichment:** Because the panels hold no names, cardholder names are imported **one-way** from
iVMS/NVMS via the `access identity` verbs (`src/DVRTool.Vendors.HikvisionIvms`): `--import-csv` (the
supported plaintext Person export — complete path), `--import-ivms` (reads the live SQLCipher DB and
correlates names to fobs by unique expiry — partial), `--capture-key`/`--where`/`--clear`. The map is
cached in a DVRTool-owned file and applied automatically to the roster/find/export verbs. The per-install
SQLCipher key is supplied by the operator at runtime (flag / `IVMS_DB_KEY` / cached key file) — **never
hardcoded**; no key capture (debugger) or write-back into iVMS is implemented, and the `Card.CardNo`
cipher is deliberately not used. Read `docs/ivms-integration-findings.md` before touching it.
