# Test fixtures

`card-records.b64` holds three real `NET_DVR_CARD_CFG_V50` records (2708 bytes each,
base64, one per line) captured live from a Hikvision DS-K2604 access panel (OEM "OCB",
firmware V2.0.004) over HCNetSDK port 8000 on 2026-08-19.

They exist because the SDK itself cannot be mocked: the parsing/marshalling layer is
tested against the exact bytes the device really sends. Card numbers are 4-digit fob
IDs; every record has an empty `byName` and `dwEmployeeNo == 0`, which is the whole
point of one of the tests — that firmware stores no cardholder identity.
