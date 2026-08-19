**MANDATORY:** Workflows and agents are allowed, but they MUST never be Fable. Opus should be used for implementing, Sonnet used for big research, and Haiku used for small tasks or local searching.

**Access control:** Hikvision/OEM door panels are supported via the `access` CLI command group
(`src/DVRTool.Vendors.HikvisionAccess`, HCNetSDK P/Invoke over port 8000). Reads are live-verified
against Site A's three OCB panels; writes are implemented and unit-tested but have never been
fired at production hardware. Read `docs/hikvision-access-control-findings.md` before touching it —
notably: these panels store **no cardholder names**, and `dwModifyParamType` plus the door/right-plan
pairing are the two traps that silently produce a card that never opens a door.
