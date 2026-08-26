# Handoff — access provisioning (onboard/offboard) driven by the iVMS-pulled policy

**Written:** 2026-08-25, from the Site A Seating Charts (operator) session.
**For:** a DVRTool session implementing the write-side automation so Site A onboarding
and offboarding no longer require opening iVMS-4200.
**Builds on:** `hikvision-access-control-handoff.md` (mission, SDK transport, safety) and
`hikvision-access-control-findings.md` (what the panels actually do — read that for the
struct/write facts). This doc adds the **policy layer** that decides *which panels/doors/schedule*
a person gets, using data pulled straight out of iVMS.

## 1. Decision that scopes this work

Operator (Josh) chose **Path B: DVRTool is the authority**; iVMS is not part of the runtime
flow. The goal is literally "never open iVMS again" for add/remove-user. iVMS's provisioning
model has therefore been **extracted to a static config** (below), and DVRTool reproduces its
decisions by writing directly to the panels over the SDK — the write path already proven in
`hikvision-access-control-findings.md` §5a (canary on .223).

Divergence is not a concern in this direction: iVMS's non-destructive "Get from Device"
re-import (operator-run, GUI) reconciles its mirror afterward if anyone still uses the GUI.
Nothing here writes toward iVMS.

## 2. Inputs (already produced — do not re-derive)

- **The policy**, freshly pulled and validated against the live panels:
  `artifacts/ivms-pull-2026-08-25/access-control-policy.json` (+ `.txt` for humans, + the six
  raw SQLCipher table dumps). `artifacts/` is gitignored — this is customer data; keep it there,
  never commit it. Schema of the JSON (one object per access group):
  ```json
  { "group": "Employes", "groupGuid": "...", "scheduleGuid": "...",
    "schedule": "(default) = 00:00:00;24:00:00;FFFF;FFFF", "memberCount": 143,
    "doors":   [ { "panelName": "ocb1", "panelIp": "192.0.2.221", "doorNo": 1, "doorName": "Front entrance_ocb1" } ],
    "members": [ { "name": "First Last", "employeeNo": "6", "personnelGuid": "..." } ] }
  ```
- **Name ↔ fob** already lives in `%LOCALAPPDATA%\DVRTool\identity-map.json`
  (`access identity --import-ivms`, 148/149 fobs; the one unmatched straggler is identified in the
  local iVMS notes — cardholder names/fobs stay out of git).
  Offboarding resolves a name to a fob through this map.
- **The panels** and creds: `OCB_PANELS` / `OCB_USER` / `OCB_PASS` in `.env` (already set).

### 🪤 The panel map is NOT in IP order — bake this in, do not infer from the octet

| iVMS name | IP | GUID | Firmware | Doors (1–4) |
|---|---|---|---|---|
| **ocb1** | 192.0.2.221 | `AAAA1111…` | V2.0.4 | Front entrance / Break room / Department office 1 / Manager office 1 |
| **ocb2** | 192.168.0.**223** | `BBBB2222…` | V2.0.4 | HR hallway / Interior hallway / Department office 2 / Department office 3 |
| **ocb3** | 192.168.0.**222** | `CCCC3333…` | V2.0.9 | Manager office 2 / IT room / Conference room / Reception |

The policy JSON already carries `panelIp` per door, so drive off that; the name↔IP table is
here only so a human reviewer isn't tripped by ocb2=.223 / ocb3=.222.

## 3. The five groups (what the policy encodes)

| Group | Members | Writes to | Schedule |
|---|---|---|---|
| Super admin | 6 | all 4 doors on **all 3 panels** | 24/7 |
| Employes | 143 | ocb1/.221 door 1 only | 24/7 |
| Group C | 4 | ocb1/.221 door 4 | ~24/7 (00:02–24:00) |
| IT | 7 | ocb3/.222 door 2 | 24/7 |
| Manager office 2 | 5 | ocb3/.222 door 1 | 24/7 |

A person may be in several groups (10 are). **Effective door rights on a panel = the UNION of
every group they belong to, projected onto that panel.** Reconciled exactly with the live panel
enumeration (findings §5): Super admin 6×3 = the 18 all-four rows, Employes = the 141 door-1
rows, and the IT/John/Group C overlaps produce the 1+2 / door-2 / 1+4 rows. .223 = the 6 Super
admins only. So the model is correct and current.

## 4. What to build

Reuse the existing seam — do NOT invent a new transport. `IAccessControlClient`
(`DVRTool.Core/AccessControl.cs`) already exposes `UpsertCardAsync(AccessCard)` and
`RevokeCardAsync(cardNo)`; `HikvisionAccessClient` implements them; `CardRecord.SetDoors` already
writes `byDoorRight` **and** the `wCardRightPlan` slot together (`DefaultRightPlan = 1`, which is
the panels' 24/7 template — the plan every live Site A card uses). The write path is proven.

Add, above that:

1. **`DVRTool.Core` — policy model + loader.**
   - `AccessPolicy` / `AccessGroupPolicy` records: group name → list of `(panelIp, doorNo, doorName)`
     + a schedule descriptor; plus a panel catalog (`name` ↔ `ip`) and a door catalog.
   - A loader that reads `access-control-policy.json` (path via flag / env / a conventional
     location) into that model. Pure; unit-testable against the committed sample? **No — the
     policy contains real names; keep it in `artifacts/` and load by path.** Unit-test the loader
     against a tiny hand-written fixture instead.
   - `AccessPolicy.ResolveGrants(groupNames)` → `IReadOnlyList<(string panelIp, IReadOnlyList<int> doors)>`:
     the per-panel door UNION for a set of groups. This is the core logic; test it hard
     (multi-group union, a group spanning panels, unknown group name → clear error).

2. **Onboard / offboard orchestration** (Core service + CLI verbs).
   - **Onboard:** inputs = person name (our `First.Last` convention), the **physical fob number**
     read off the card (`--card`, NOT auto-generated), and one or more `--group` names. Steps:
     resolve grants → for each panel, build an `AccessCard { CardNo, Doors = union, Valid = true,
     ValidFrom/Until }` and call `UpsertCardAsync` on that panel's client → then record name↔fob in
     the identity map. Give every card a **validity window** (iVMS gave ~10-year windows;
     an unbounded fob is exactly what offboarding must avoid — default to a long but bounded
     window, `--valid-until` to override).
   - **Offboard:** input = person name. Resolve name→fob via identity-map → `RevokeCardAsync` on
     **all three** panels (findings: revoke = delete; safe if absent) → drop the identity-map entry.
     Offboarding must query/act on all panels regardless of group, since a stale fob could linger.
   - **Reconcile (read-only, build this first):** compare the policy's expected per-panel card sets
     against a live enumeration (`GetCardsAsync`) and print drift. This is the safe way to prove the
     policy loader + resolver are correct against production without writing anything.

3. **Schedule handling — documented simplification.** Every Site A group is effectively 24/7,
   which is exactly `DefaultRightPlan = 1` (the plan all live cards carry). Implement grants with
   plan 1 and **do not** attempt to reproduce named schedule templates or the Group C 00:02 quirk in
   this pass; instead, **detect** any group whose schedule is not 24/7 and emit a warning that its
   time restriction is approximated as always-on. (A faithful plan-template writer is a later, separate
   task — it needs the panel-side schedule/plan download model, not modeled yet.)

## 5. CLI surface (extend `AccessCommands.cs`, match existing verbs)

```
dvrtool access reconcile   --policy <path> [--panel all]          # read-only drift report
dvrtool access onboard     --name First.Last --card <fob> --group Employes [--group IT] [--valid-until yyyy-MM-dd] [--dry-run|--force]
dvrtool access offboard    --name First.Last [--dry-run|--force]
```

`--dry-run` (default for safety) prints the exact per-panel writes/revokes it *would* perform;
`--force` is the explicit consent gate, mirroring the repo's `AtomicDownload`/`grant` discipline.

## 6. Safety — hard boundaries for THIS build

1. **Do not fire writes at the live panels in this implementation task.** Build the code, run the
   **unit tests**, and validate only with `--dry-run` and the read-only `reconcile`. The first real
   write (a throwaway test fob on the quiet panel **.223 / ocb2**, created then revoked, byte-diff
   clean) is a **separate step the operator runs with go-ahead** — do not do it autonomously.
2. **Lockout is shared with iVMS** (same source IP, ~5 fails → ~30 min). Creds are known-good in
   `.env`; never loop logins. `SetReconnect(_,0)`, always `Logout`+`Cleanup` (already in the client).
3. These are **physical doors.** `--dry-run` is the default; `--force` is required for any write.
4. **Never commit `artifacts/`** (customer names/fobs) or any key. `identity-map.json` and the
   SQLCipher key stay in `%LOCALAPPDATA%`.
5. Do **not** commit the work — leave it on the working tree for operator review (there are already
   uncommitted changes on `main` from the CardNo-decode session; build on top, don't revert them).

## 7. Definition of done

- `dvrtool access reconcile --policy artifacts/ivms-pull-2026-08-25/access-control-policy.json`
  loads the policy, enumerates the three live panels (via the relay host deployment), and reports zero
  or explained drift — proving the resolver matches reality with **zero writes**.
- `access onboard … --dry-run` prints, for a sample person+groups, the correct per-panel door
  union (e.g. an IT+John-Office person → .222 doors 1 and 2; an Employes person → .221 door 1).
- `access offboard … --dry-run` resolves a known name to its fob and lists a revoke on all three
  panels.
- Unit tests cover: policy load, `ResolveGrants` union/multi-panel/unknown-group, and the
  onboard/offboard command building the right `AccessCard`/revoke set from a mocked policy +
  identity map. No live-panel calls in tests.
- A short note appended here (or a sibling findings doc) on how to run the .223 canary when the
  operator green-lights the first real write.

## 8. Deployment note (for when writes are approved)

DVRTool runs live on **the relay host** (on the panel LAN). Publish self-contained win-x64 and push
via the remote-agent relay (see `hikvision-access-control-handoff.md` §3/§6). The SDK resolves from
the HikCentral Lite / iVMS install paths, or set `OCB_SDK_DIR`.

## 9. Status — IMPLEMENTED (2026-08-25), writes NOT yet fired

The policy layer, the resolver, and the onboard/offboard/reconcile verbs are built and unit-tested
(51 new tests, whole suite green). **No write has been sent to a live panel** — that is §10, gated
on operator go-ahead. What landed:

- `DVRTool.Core`: `AccessPolicy` + loader (`AccessPolicy.cs`), the door-union resolver
  `ResolveGrants`, the `AccessSchedule.Is24x7` detector, the pure onboard/offboard planners
  (`AccessProvisioner.cs`), the read-only drift engine (`AccessReconciler.cs`), and
  `IdentityMap.FindByName/With/Without` (`Identity.cs`).
- `DVRTool.Cli`: `access reconcile | onboard | offboard` in `AccessCommands.cs`; `--group` now
  repeats and `--dry-run` is a recognised flag (`Program.cs`).
- Schedule handling is the documented simplification: grants are plan-1 (24/7); a non-24/7 group is
  written **always-on with a warning** (the live "Group C" group, `00:02:00` start, triggers it).

`--dry-run` is the default for onboard/offboard; a write needs `--force` **and** no `--dry-run`.
Verified dry-run outputs (no panel contact): Employes → `.221` door 1; IT + Manager office 2 → `.222`
doors 1,2; Super admin → all 4 doors on `.221/.222/.223`; unknown group → clear error.

## 10. First live write — the `.223` / ocb2 canary (run with operator go-ahead only)

The low-level write path (`UpsertCardAsync` / `RevokeCardAsync`) is already proven on `.223` — see
`hikvision-access-control-findings.md` §5a. This is the procedure for the **provisioning-verb**
canary, on the quietest panel (6 fobs), from **the relay host** with the published exe:

1. **Read-only first.** `dvrtool access reconcile --policy artifacts\ivms-pull-2026-08-25\access-control-policy.json`
   must load, read all three panels, and report zero or explained drift. Zero writes; this proves the
   resolver matches reality before anything is written.
2. **Snapshot `.223`.** `dvrtool access export --panel 192.0.2.223 --out before.csv` (customer data —
   keep it under `artifacts\`, never commit).
3. **Dry-run the canary.** Use a throwaway fob that is **not** in use (findings used `9001`) and the
   lowest-level verb so the canary touches `.223` only — `access grant --card 9001 --doors 1
   --panel 192.0.2.223 --name Test.Canary` (no `--force`) — and read the planned write.
   (`onboard` targets whichever panels a group maps to; only *Super admin* reaches `.223`, which is
   too broad for a canary. For an onboard-verb canary instead, hand-write a one-group policy whose
   only door is `192.0.2.223` door 1 and `onboard --policy that.json --card 9001 --group <it>
   --dry-run`.)
4. **Write it.** Re-run the grant with `--force`. The verb re-reads the fob and prints the verified
   state; confirm `valid=yes doors=1`.
5. **Verify independently.** `dvrtool access find --card 9001` shows it present on `.223` only.
6. **Revoke.** `access revoke --card 9001 --force` (or `offboard` if the fob was mapped). Revoke =
   delete on these panels; the record disappears from the enumeration.
7. **Byte-diff clean.** `dvrtool access export --panel 192.0.2.223 --out after.csv` and diff against
   `before.csv` — they must be identical, exactly as findings §5a saw. That is the rollback proof.

Safety in force the whole time (`hikvision-access-control-handoff.md` §4): one login attempt per
panel — the calling IP shares iVMS's lockout, so never loop; always `Logout`+`Cleanup` (the client
does); and an SDK write lands on the panel while iVMS stays unaware until its next "Get from Device"
— irrelevant for a throwaway fob revoked before it matters, but decide sync direction before routine
provisioning.

## 11. Run record — first live write DONE, byte-clean (2026-08-26)

The `.223` / ocb2 canary in §10 was executed on **the relay host** (the panel-LAN machine, via the
remote-agent relay) with the shipped `C:\Program Files\DVRTool\cli\dvrtool.exe`, operator go-ahead
given. **This is the first live write to a physical panel, and it rolled back byte-clean.** Sequence
and results:

1. `access reconcile` — reached and serial-pinned all three panels; **`.223` in sync (6/6)**. `.221`
   (144 vs 149) and `.222` (14 vs 15) showed **explained** drift only: 5 policy members have no
   unique fob in the identity map (fobs incl. the known straggler), so their live fobs read as
   "extra". Not a resolver error — an identity-map completeness gap. Resolver matches reality.
2. Snapshot `.223` → `before.csv` (6 fobs). Confirmed fob **9001 absent fleet-wide** before touching it.
3. Dry-run `grant --card 9001 --doors 1 --panel 192.0.2.223` → planned `.223`-only add; refused
   without `--force`, as designed.
4. Write `grant … --valid-until 2026-08-27 --force` → client re-read and **verified** `fob 9001
   doors=1 valid=yes`. (Gave it a bounded window rather than the unbounded default, defensively.)
5. Independent `find --card 9001` → present on **`.223` only**, active.
6. `revoke --card 9001 --force` → verified **gone**; `find` confirms absent fleet-wide.
7. Re-export `.223` → `after.csv`; **SHA-256 of before == after** (byte-identical). Rollback proof, as
   findings §5a saw with the low-level path.

The whole provisioning write path (`grant`/`revoke` → `UpsertCardAsync`/`RevokeCardAsync`, identity
guard, `--force` gate, verify-after-write) is now **proven end-to-end on live hardware.** The scratch
working dir (`.env` + policy + CSVs — a secret and customer data) was staged for the run and **deleted
after**; nothing customer-side was committed. Routine provisioning is unblocked; before using
`onboard` for real hires, complete the identity map (the 5 unmapped holders) so reconcile reads fully
in sync, and decide the iVMS sync direction (§10 safety note) if the GUI is still used.
