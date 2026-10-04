# Tools

Read-only scripts that answer questions about the shipped game build, so a change to it can be traced without opening a decompiler. They read `NuclearOption_Data\Managed\Assembly-CSharp.dll` through the Mono.Cecil that BepInEx ships, and they need the game path, from `NUCLEAR_OPTION_ROOT` or from `-NuclearOptionRoot`.

| Script | What it answers |
|---|---|
| `Find-GameTypes.ps1` | Which types match a regex, with `-Methods` for their methods |
| `Find-MemberUsage.ps1` | Who reads a field or calls a method, given only the name, with the type that declares it. Built for the obfuscated parts of Rewired |
| `Find-StringUsage.ps1` | Which method loads a string constant that matches a regex |

The most useful one after a game update is `Find-MemberUsage`, since this mod reaches into the lobby and Steam code by member name in places, and a rename shows up as a warning in the log with no way to find the new name by reading.

## Examples

```powershell
$env:NUCLEAR_OPTION_ROOT = 'D:\SteamLibrary\steamapps\common\Nuclear Option'

.\Find-GameTypes.ps1 -Pattern '^SteamLobby$' -Methods
.\Find-GameTypes.ps1 -Pattern 'Lobby|Lobbies' -Methods
.\Find-MemberUsage.ps1 -Pattern 'm_bPassword'
.\Find-StringUsage.ps1 -Pattern 'join_lobby'
```
