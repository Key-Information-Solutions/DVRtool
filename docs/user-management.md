# DVR/NVR login accounts

The accounts people log into a *recorder* with — not the fobs on a door panel, which are
`docs/hikvision-access-control-findings.md`. Two surfaces: the GUI **Users** tab in
*DVR / NVR user accounts* mode, and `dvrtool users`.

Reads work on Hikvision and Dahua. **Writes are Hikvision only, and add-only.**

## 1. Why add-only

Creating an account cannot lock anyone out of a customer recorder. Deleting one, changing a
password, or changing a level all can — and on a fleet reached through one shared admin
account, the blast radius of getting it wrong is every site that account opens. So the first
write release creates accounts and does nothing else.

The corollary, which the confirmation dialog says out loud: **there is no undo in DVRTool.**
An account created from here is removed on the recorder's own web UI. Fire canaries at the lab
recorder, where cleanup is free.

The second rule falls out of the first: **an account that already exists is never touched**,
not even when its level differs from the one asked for. `UserAddPlan` puts it in
`AlreadyPresent` with the level the device holds it at, and reports it. Add-only silently
becoming "modify" is the one mistake here that hands somebody rights nobody chose to give them.

## 2. Passwords

Prompted at write time, applied to every recorder in the run, and **never stored**: no config
file, no pin store, no log line, and deliberately **no `--password` flag** — a password on the
command line persists in shell history, in process listings and in audit logs.

One prompt per run is also what makes the accounts genuinely match. Both vendors treat
passwords as write-only and never return them, so the tool can confirm that an account *exists*
on two recorders but can never confirm that the two share a password. Creation time is the only
moment at which "identical accounts" is enforceable, and it is enforced by only asking once.

`UserPasswordRules.Check` (Core, pure) is the local floor: 8–16 characters, at least two
character classes, not the account name or its reverse. It is deliberately the documented
Hikvision minimum and **not** a house policy — a stricter rule would refuse passwords the
recorder would have taken, and the operator would have no way to tell which of the two was
complaining. The device stays the authority: it can still reject a password this accepts, and
when it does, its own words are what gets shown (§3).

## 3. Hikvision: `POST /ISAPI/Security/users`

`HikvisionClient.Users.cs`. Body:

```xml
<User><id>0</id><userName>…</userName><password>…</password><userLevel>Operator</userLevel></User>
```

Three things to know before changing any of it.

**`<id>0</id>` is a placeholder.** The device assigns the real id and echoes it back. Omitting
the element entirely is refused on some firmware, so it is always sent.

**A refused password comes back as HTTP 200.** ISAPI answers a `ResponseStatus` document, and
`statusCode` 1 is the only success — a weak-password rejection arrives under a 200 with a non-1
`statusCode` and a `subStatusCode` of `riskPassword`. A create that checks only the HTTP status
reports success for an account that was never made. This is the same trap as the bitrate PUT in
`HikvisionClient.Storage.cs`; `TryParseStatusCode` is shared, and `DescribeRejection` carries
`subStatusCode` / `statusString` / `errorMsg` through **verbatim**, because the device's own
words are the only useful thing to show an operator here.

**The create is read back.** An accepted POST that leaves no account in the list is a failure,
not a success, and throws. The read-back is also where the reported id and level come from: the
recorder may keep a level other than the one asked for, and what gets printed is what it kept.

**Permissions are not written.** The fine-grained rights at
`/ISAPI/Security/UserPermission/<id>` are left alone: `userLevel` carries the firmware's own
default permission set, and the permission schemas vary by firmware in ways this tool has not
verified. Writing a guessed permission document is the difference between an account that works
and one that silently opens nothing. If a firmware turns out to need an explicit permission POST
for a created Operator to see video, that is a follow-up — confirm it by logging in as the
created account before assuming it.

## 4. Dahua, Nx

Dahua reads (`userManager.cgi?action=getUserInfoAll`) and is not written. `addUser` is
query-string with a plaintext password, and no write of any kind has ever been fired at a Dahua
recorder from this tool — doing a first-ever Dahua write and a first-ever user write at the same
time doubles the unknowns, so it waits. Note also that Dahua's `modifyPassword` requires
`pwdOld`, so there is no blind admin reset there whenever password changes do get built.

Nx has no user client at all. `IUserAdminClient` is a separate interface from
`IUserManagementClient` for exactly this reason — the same split as `IDeviceConfigWriter` from
`IDeviceConfigClient`. A vendor that can be read is not thereby a vendor that can be written,
and both front ends ask before offering the button: the GUI disables **Add user…** and names the
recorder in the way, the CLI refuses before reading anything or prompting for a password.

## 5. Lockout

Roughly five failed logins puts an account into a ~30 minute lockout on both vendors. Nothing
here retries a 401, and a fleet add reads every recorder once before writing. If a write leg
fails on one recorder, the others still run and the failures are reported at the end — but do
not loop a failing `users add` over a fleet.

## 6. Shape

The same shorthand as every other fleet write in this repo:

1. `UserAddPlan.For(read, name, role)` in Core — pure, no I/O, built from reads that already
   happened. Both front ends describe the write from this one piece of arithmetic.
2. Gate: `--force` on the CLI (`--dry-run` is the default and wins when both are given); on the
   GUI, a `MessageBox` that names every recorder and **defaults to No**.
3. Each write leg: fresh client → `DeviceIdentityGuard.Ensure` (a second login — between the
   read and the write, the address could be answering elsewhere) → create → read back.
4. Re-read the fleet afterwards so the grid shows the result rather than the intention.

One GUI detail worth remembering: `ShowFleetMatrix` sets its own summary status, so a status
message set *before* re-rendering the grid is overwritten and the operator is told nothing.
Render first, then speak.

## 7. Verified

- Plan arithmetic, password rules, and the Hikvision POST/read-back/rejection paths: unit
  tests (`UserAddPlanTests`, `UserPasswordRulesTests`, `HikvisionUserWriteTests`).
- CLI live, 2026-09-17: dry run against the lab recorder; the fleet list across Site A's two
  recorders; the add-only guard planning an existing account (`donna`, asked at Admin, reported
  as Operator on both, nothing to create); refusals for an unwritable vendor, an unknown role, a
  missing name, and `--force` with redirected input.
- GUI live, 2026-09-17: the button disabled before a load and enabled after; the dialog's
  password complaints and mismatch; the confirmation naming both recorders with No focused;
  Cancel leaving all four accounts untouched.
- **Not yet fired: any real create.** The first one should be a canary on the lab recorder,
  followed by logging in as it to confirm the level alone is sufficient (§3).
