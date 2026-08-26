using DVRTool.Core;

namespace DVRTool.Tests;

/// <summary>
/// A tiny, entirely synthetic access policy for the provisioning tests.
/// </summary>
/// <remarks>
/// Hand-written on purpose: the real <c>access-control-policy.json</c> holds customer
/// cardholder names and lives only in the gitignored <c>artifacts/</c> tree, so it is never
/// committed or loaded from a test. Names here are Greek-letter placeholders; IPs are RFC-1918
/// stand-ins. The shape mirrors production: one broad group, one admin group that spans two
/// panels, and one non-24/7 group — with a person (Alpha) deliberately in two groups so the
/// per-panel door UNION is exercised.
/// </remarks>
internal static class AccessPolicyFixtures
{
    internal const string Json = """
        [
          {
            "group": "Everyone",
            "groupGuid": "GG-EVERYONE",
            "scheduleGuid": "SG-24x7",
            "schedule": "(default) = 00:00:00;24:00:00;FFFF;FFFF",
            "memberCount": 2,
            "doors": [
              { "panelName": "north", "panelIp": "10.0.0.1", "doorNo": 1, "doorName": "Front_north" }
            ],
            "members": [
              { "name": "Alpha Uno", "employeeNo": "1", "personnelGuid": "G-ALPHA" },
              { "name": "Beta Dos",  "employeeNo": "2", "personnelGuid": "G-BETA" }
            ]
          },
          {
            "group": "Admins",
            "groupGuid": "GG-ADMINS",
            "scheduleGuid": "SG-24x7",
            "schedule": "(default) = 00:00:00;24:00:00;FFFF;FFFF",
            "memberCount": 1,
            "doors": [
              { "panelName": "north", "panelIp": "10.0.0.1", "doorNo": 2, "doorName": "Back_north" },
              { "panelName": "south", "panelIp": "10.0.0.2", "doorNo": 1, "doorName": "Vault_south" }
            ],
            "members": [
              { "name": "Alpha Uno", "employeeNo": "1", "personnelGuid": "G-ALPHA" }
            ]
          },
          {
            "group": "NightGuard",
            "groupGuid": "GG-NIGHT",
            "scheduleGuid": "SG-NIGHT",
            "schedule": "Restricted = 22:00:00;06:00:00;FFFF;FFFF",
            "memberCount": 1,
            "doors": [
              { "panelName": "south", "panelIp": "10.0.0.2", "doorNo": 2, "doorName": "Gate_south" }
            ],
            "members": [
              { "name": "Gamma Tres", "employeeNo": "3", "personnelGuid": "G-GAMMA" }
            ]
          }
        ]
        """;

    internal static AccessPolicy Policy() => AccessPolicy.Parse(Json);
}
