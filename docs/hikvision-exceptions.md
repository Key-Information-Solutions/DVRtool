# Hikvision exceptions: who hears about an illegal login

Read this before touching `src/DVRTool.Core/EventNotification.cs`,
`src/DVRTool.Core/ExceptionAudit.cs`, `HikvisionClient.Exceptions.cs`, the CLI's
`exceptions` group or the Config tab's **Fleet e-mail audit** panel.

Everything below was read off live firmware on **2026-09-18**, read-only, on three models
plus a full fleet sweep of seventeen:

| target | what it is | firmware |
|---|---|---|
| the lab recorder | DS-7716NI-I4/16P(B), I-series NVR | V4.61.030 build 240123 |
| Site C | DS-9632NI-M8, M-series NVR | V5.04.081 build 260112 |
| Site G | iDS-7316HUHI-M4/S, hybrid DVR | V4.51.100 build 230906 |
| Site H | DS-7616NI-Q2/16P, Q-series NVR | V4.83.006 build 240910 |

**No write of any kind has been issued by this feature, and none is implemented.**

## 1. The question, and why the recorder cannot answer it

"Which of my recorders would e-mail me if somebody started guessing passwords on them?"

That is two facts, and a Hikvision recorder keeps them on two different pages:

1. **Configuration → System → Event → Exception** — the exception type, and a row of
   checkboxes: Audible Warning, Notify Surveillance Center, **Send Email**, Trigger Alarm
   Output.
2. **Configuration → Network → Advanced → Email** — the SMTP server, the sender and the
   recipients.

A recorder with Send Email ticked and a blank SMTP page **looks configured on the page an
operator would check**, sends nothing, and reports nothing — no error, no log line, no
symptom until the night somebody needed the mail. Three recorders in our own fleet are one
tick away from exactly that state, and one (Site C) is the only one of seventeen that
genuinely mails. Auditing this from outside the box is the whole point;
`ExceptionEmailState.GoesNowhere` is the state that only an outside read can see.

## 2. The three documents

All GETs, all answering on every firmware above.

### `/ISAPI/Event/capabilities` — does this firmware have the exception at all

An `isSupportX` roster. The flags that matter here, with what they gate:

| flag | event type | lab I-series | M-series | hybrid DVR |
|---|---|---|---|---|
| `isSupportIllAccess` | `illaccess` | true | true | true |
| `isSupportHDFull` | `diskfull` | true | true | true |
| `isSupportHDError` | `diskerror` | true | true | true |
| `isSupportNicBroken` | `nicbroken` | true | true | true |
| `isSupportIpConflict` | `ipconflict` | true | true | true |
| `isSupportRecordException` | `recordingfailure` | true | true | true |
| `isSupportViMismatch` | `videomismatch` | false | false | **true** |
| `isSupportSpareException` | `spareException` | false | **true** | false |
| `isSupportRaidException` | `raidexception` | false | false | false |
| `isSupportViResMismatch` | `resolutionmismatch` | false | false | false |
| `isSupportPOCException` | `pocException` | false | false | false |

**`isSupportViException` is deliberately unpaired.** It reads `false` on the DVR that
nonetheless lists a working `badvideo` trigger, so pairing the two would report a live
exception as unsupported — the one answer this audit must never give. `ExceptionTypes`
therefore maps only the pairs that were confirmed both ways, and an unpaired flag is
dropped rather than guessed at.

The consequence for the design: **the capabilities flag and the trigger list are two
independent pieces of evidence**, and `ExceptionTrigger.Exists` is `Declared ?? Listed` —
the flag wins when there is one, the list stands on its own when there is not.

### `/ISAPI/Event/triggers` — what happens when it fires

One list, every trigger on the box: 102 entries on a 16-channel NVR, 251 on the 16-channel
DVR. Each entry is an event and the notification methods currently ticked:

```xml
<EventTrigger>
<id>illaccess</id>
<eventType>illaccess</eventType>
<EventTriggerNotificationList>
<EventTriggerNotification><id>email</id><notificationMethod>email</notificationMethod></EventTriggerNotification>
</EventTriggerNotificationList>
</EventTrigger>
```

**Presence is the switch.** A notification that is off is simply absent, so an empty
`EventTriggerNotificationList` means the exception fires and *nothing happens* — which is
what fifteen of our seventeen recorders say about illegal login. Methods seen in the field:
`email`, `beep`, `center`, `record`, `IO`, `whiteLightOut`.

**A device-level exception is the one with no channel.** Camera events in the same list
carry `dynVideoInputChannelID` (NVR) or `videoInputChannelID` (DVR), and alarm inputs carry
`inputIOPortID`; the recorder's own faults carry none. That rule — not a hardcoded roster —
is what `ExceptionTypes.IsDeviceLevel` implements, so a firmware that adds an exception type
is reported rather than silently dropped. It also matters for correctness, not just
completeness: on one DVR a *per-camera* `videoloss` trigger has `email` ticked, and a filter
that counted it would report illegal-login alerting the recorder does not have.

### `/ISAPI/System/Network/mailing` — would the mail arrive

```xml
<mailing><id>1</id>
  <sender><name>…</name><emailAddress>…</emailAddress>
    <smtp><enableAuthorization/><enableSSL/><addressingFormatType>hostname</addressingFormatType>
          <hostName>…</hostName><portNo>587</portNo><accountName>…</accountName></smtp>
  </sender>
  <receiverList><receiver><id>1</id><name/><emailAddress>…</emailAddress></receiver>…</receiverList>
</mailing>
```

Traps:

- **The firmware ships three empty `<receiver>` slots.** An empty `emailAddress` is a
  placeholder, not a recipient; counting the elements would call every blank mail page
  configured.
- `addressingFormatType` says which of `hostName`/`ipAddress` is live, but whichever is
  filled wins in the parser and the declared type only breaks ties — the same
  "trust the filled field" rule the port documents already needed.
- The sender address is **not** required for `Configured`: relays that accept an empty
  sender exist. It is named in `EmailDelivery.Gap` so an operator still sees it missing.
- There is no `isSupportEmail` anywhere. `/ISAPI/System/Network/capabilities` carries FTP,
  DDNS, PPPoE, SNMP and 802.1x flags and nothing about mail, so "can this recorder e-mail"
  is answered by whether `mailing` answers at all.

## 3. The 503 trap: "Device Busy" means "no such trigger"

Asking for one trigger by id answers **503 Service Unavailable** when the id does not exist:

```
GET /ISAPI/Event/triggers/notARealEventType
→ 503  <statusString>Device Busy</statusString>
       <subStatusString>isapi get event trigger is failed[131073]!!!</subStatusString>
```

`/ISAPI/Event/triggers/capabilities` answers **the same 503 on every firmware tried** — for
the same reason: there is no such node. So a 503 from anywhere under `/ISAPI/Event/triggers/`
is "no such trigger", not a busy recorder, and code that backs off and retries it waits
forever for a node that will never exist.

`HikvisionClient.GetExceptionsAsync` therefore **never asks for a single trigger by id**. It
reads the list, which is one request, cannot lie this way, and costs less than the six
per-type GETs it replaces.

## 4. What the fleet actually said (2026-09-18)

`dvrtool exceptions audit --all-saved`, seventeen Hikvision recorders, **zero failed reads**:

- **17 of 17** have the Illegal Login exception. It is not a firmware-dependent feature on
  anything we have deployed — the original worry does not survive contact with the fleet.
- **1 of 17** actually e-mails on it (Site C, to one address).
- **16** have it and do not use it. Thirteen of those have working mail settings already —
  one tick each.
- **3** of those sixteen have **no mail settings at all**: the tick alone would not help
  them, which is why the summary counts them separately.
- Four saved recorders are Dahua or Nx and were **not read** — reported as not implemented,
  never as "no alerts".

## 5. Scope: read-only, Hikvision-only, on purpose

- **No writes.** Ticking a notification is a fleet-wide change with no undo inside DVRTool,
  and the audit repays its build without one. The fix is a tick on the recorder's own
  Exception page. `IExceptionNotificationClient` is a read interface, so a future
  `IExceptionNotificationWriter` is the same sibling split `IDeviceConfigWriter` and
  `ICameraSettingsWriter` already use.
- **Hikvision only.** Dahua keeps event linkage in `configManager.cgi` under different names
  and Nx keeps it in its rule engine; neither has been read for it. Those rows say so.
  "Not implemented" and "nothing configured" must never print the same thing.
- **Not audited: whether the mail actually sends.** The recorder's own "Test" button on its
  mail page is the only thing that proves the credentials and the relay, and DVRTool does
  not press buttons that send mail from a customer's device.

## 6. Where the code is

| piece | file |
|---|---|
| Models, labels, the device-level rule | `src/DVRTool.Core/EventNotification.cs` |
| Verdicts, states, fleet summary, sweep | `src/DVRTool.Core/ExceptionAudit.cs` |
| The three reads and their parsers | `src/DVRTool.Vendors.Hikvision/HikvisionClient.Exceptions.cs` |
| `dvrtool exceptions show \| audit` | `src/DVRTool.Cli/ExceptionCommands.cs` |
| Config tab → **Fleet e-mail audit** | `src/DVRTool.App/MainWindow.Exceptions.cs` |
| Fixtures from the live captures | `tests/DVRTool.Tests/ExceptionAuditTests.cs` |
