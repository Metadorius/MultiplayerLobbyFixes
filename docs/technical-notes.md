# Technical notes

What the mod hooks, why, and what it changes about vanilla behaviour. Written for anyone reading the code or wondering why something behaves the way it does.

## Build and launch

```powershell
dotnet build src\MultiplayerLobbyFixes\MultiplayerLobbyFixes.csproj
```

The build needs your game path. It resolves `-p:NuclearOptionRoot=...` first, then `Directory.Build.user.props`, then the `NUCLEAR_OPTION_ROOT` environment variable, and stops with an explanation rather than guessing. Copy `Directory.Build.user.props.example` to get started. By default the build copies the DLL into `BepInEx\plugins\MultiplayerLobbyFixes\`, and `-p:DeployToPlugins=false` skips that.

BepInEx loads plugins once per process, so a rebuild needs a game restart. The log line `Loaded build: ... (mtime HH:MM:SS)` names the DLL it actually loaded, which is the quickest way to tell whether the build you just made is the one running. At startup the mod also logs a patch report: a count per patched member, then the current settings values. A count of `-1` in that report means the patch did not find its method.

`tools\Find-GameTypes.ps1` indexes the game's types through Mono.Cecil from BepInEx, handy for finding things without opening a decompiler:

```powershell
.\tools\Find-GameTypes.ps1 -Pattern 'SteamLobby|LobbyList' -Methods
```

`.vscode\launch.json` builds, deploys and starts the game. The lobby work happens in the main menu and the multiplayer screen, and a Steam join starts from the Steam client, so the launch is a plain one: start the game, then drive the join from Steam.

The install path is the `nuclearOption.root` setting in `.vscode\settings.json`, which the build task uses too, so one edit covers a moved game. The file is gitignored because it is a machine path, and without the key the build and launch stop on an unresolved variable. `NUCLEAR_OPTION_ROOT` cannot stand in, since VS Code resolves `${env:...}` against the environment it started with, so a later variable never reaches tasks or launches.

## What 0.34.6 changed

The server list communication was rebuilt, and most of what this mod used to carry has no work left to do.

A refresh is one `RequestInternetServerList` call. Steam queries each server's query port and answers per server, through `ServerListRequest.OnServerResponded` or `OnServerFailedToRespond`. What used to be a second round trip per server now rides along: `ServerLobbyInstance.SetDetails` parses the server's game tags (`DedicatedServerKeyValues.ParseTags`) into its rules, which supplies the map id, start time, password flag and host ping location, while the entry's own details supply the mission, name and player count. `CalculatePing` takes the entry's ping, or the `HostPing` tag turned into an estimate. Server operators build those tags in `DedicatedServerKeyValues.ApplyTags`, which pushes them through `SteamGameServer.SetGameTags`.

The consequences worth knowing:

- Nothing is queried per server while the browser is open. The rules query exists only on demand, from `SteamLobby.QueryServerRules`, which the details window calls when it opens on a server whose rules it does not already hold, and again before a join. A ping on demand exists too, and the details window asks for one every ten seconds. Both are cancelled by the window's own token when it closes.
- A refresh no longer empties the list. `RefreshLobbyListCleared` only resets the received counter and raises the overlay; `PruneStaleLobbies` removes, at refresh complete, the entries that a request did not deliver. So the rows stay on screen through a refresh by themselves.
- Filters are applied locally, in `LobbyList.MatchesFilter`, and the master request also carries them (version, password, full, empty).
- A press during the 3 s cooldown is saved as `pendingSearch` and run when it expires, instead of being dropped.
- The per-frame driver is gone. `LobbyList.Update` no longer exists; the time column is refreshed by `UpdateTimeLoop`, a one-minute delay loop.

## The server browser

What is left to fix is the server that Steam's master list knows about and this machine cannot query at all, usually one behind a relay or a firewall where the game's own connection goes through Steam while a direct query never comes back. Vanilla drops it (`OnServerFailedToRespond` only logs "Request Server List, fail index") so the browser is missing servers that are up and joinable.

`ServerListFailurePatch` postfixes that callback. A direct join's server is rescued there (below). For the browser, a failed entry becomes an entry built by hand from the copy Steam handed over, which already carries the name, the player count, the password flag and the tags. It is marked in `ServerBrowser.Unreachable`, `InList` is set so the refresh's own prune keeps the row, and the browser's update event is fired, which is all vanilla does for a server it lists. The mark comes off as soon as the server answers anything, which is what `ServerListAnswerPatch` is for. Nothing is queried for these servers.

The copies are repaired on the way in by `ServerDetailsPatch`, which prefixes `SetDetails`: Steam answers for a server it could not reach with zeroes in both count fields, no password flag, an address where the name was, no ping and no game tags, and `SetDetails` writes every one of those over what the last good copy said. `ServerBrowser.Repair` puts back the fields that have nothing usable in them, from the last counts, password flag, name, ping, game tags and mission text it saw, and leaves a copy that carries values alone, so a server that really emptied out or changed its name still shows that. The tags matter most: the map, mission, description, start time and password all live in there, so a refresh that cannot reach a server would otherwise empty its row. The repair runs before `SetDetails` parses them, which is what keeps the parsed fields, the map name lookup included, consistent.

`ServerPingPatch` blanks `CalculatePing` only for a server this machine could not reach and never measured, rather than showing the copy's placeholder. A number measured earlier stands, and so does one the game measured itself, so a server that stops answering keeps its ping and only gains the warning triangle.

`StaleRows` draws the state on the rows and in the details window: a warning triangle with a tooltip for a server the master list could not reach, in the ping column, a sync icon while that one server is being asked about, and, in the mission cell, a triangle for a rules query that came back empty and a sync icon while it is out. The ping mark rides `LobbyListItem.SetPingTextAndColor`, which vanilla calls when a row is shown and when a ping arrives; the mission mark rides `LobbyListItem.Show`, which is the only time the game writes that cell. The details window's cells are written by its own loop every second, so they are composed again each frame and only written when they differ.

Two things need a per-frame hook that the list no longer has, so `ModHost` provides one of its own, with `LobbyListFocus` remembering the browser from `LobbyList.Awake` until its `OnDestroy`: `FullServerJoin` keeps the details window's Join button clickable when the count reads full (vanilla sets `joinButton.interactable` from the player count in its own loop, and a disabled uGUI button cannot be clicked at all), and the details window's markers and Refresh button are drawn there.

`LobbyRefreshButtonPatch` adds the details window's own Refresh button. It asks the master list about that one server, which is the same call the game's own join lookup makes: `RequestInternetServerList` with a single `steamid` filter, answered through the same callbacks a refresh uses. That one request brings back a whole `gameserveritem_t`, so name, counts, ping, mission and tags all come at once, and `ServerQuery` releases the handle once the request stops refreshing. A server that fails the probe keeps everything and gains the triangle. The button is grey while the answer is out, for `RefreshCooldownSeconds`, and while the browser's own Refresh button is grey, so the two cooldowns read as one.

The rules query is asked alongside the master one, not after it: the two are independent, and the master copy's `SetDetails` skips clearing the entry's rules while a rules query is out (`if (!RulesLoading)`), so the rules answer can arrive before, during or after the master copy without either being lost.

That rules query exists because the Steam game tags are capped at 128 bytes, so the description chunks (`d0` to `d3`), the mission name and the workshop id never fit in them: those only ever arrive from a rules answer, through `ServerRulePatch` on `ServerLobbyInstance.SetRule`. `ServerBrowser.Remember` keeps every rule a server reports and `KeepRules` puts back the ones a copy did not carry, so a read of the card shows what it showed before plus whatever is new, instead of an empty description and a default image. A key the tags do carry still wins, so a mission the server has left is not resurrected.

## Steam joins

A join by Steam ID runs the game's `CliJoinHandler`: ask the master list for that one server, let Steam probe it, then let a rules query answer, and only then connect. Each step can fail on its own, and each failure has a patch:

- the probe fails (the common case): the join is rescued off the master list's copy, which carries the Steam ID the join connects to and the tags its version and password checks read. A password-protected server still asks its rules first, because the password check lives there, and one that never answers gives up after ten seconds. This is `ServerListFailurePatch` with its per-frame `TickRescue`.
- the rules query fails: `RulesFailureJoinRescuePatch` skips vanilla's "could not be found" and joins off the copy, version and password included. That rules query is now the only per-server one a listing can still start, and it only runs for a direct join.
- the refresh finds nothing: `JoinRetryPatch` asks the master list once more before the game's verdict stands, which is what a player retrying by hand does.
- the lobby's data never arrives: `SteamJoinWatchPatch` watches the pending join, re-requests the data after eight seconds, and after two attempts gives up with the game's own "Lobby no longer exists or Steam is unavailable" message instead of waiting forever.
- the join is refused: `JoinFailurePatch` shows the reason the game's own code produces ("Server full" and the rest) outside the server browser too, where vanilla keeps it silent, and consumes the reason the game armed for its main menu so one failure cannot be announced twice. It only speaks for joins it saw start and never connect: a disconnect from a session that did connect keeps vanilla's handling, a stop the local side caused (a quit, a leave for another join) is not a refusal and stays silent too, and a reason that was already pending is not consumed, so it still reaches the menu.
- the click lands while a game is already running: the join goes through the same `InviteJoinModal` the friends-list invites use, which asks before leaving ("Leave current game and join friend?", with the kick count when hosting others) and stops the network before the join. `LeaveThenJoinPatch` backs up the one state the modal skips its leave for: a join that reaches `TryJoinLobby` while the encyclopedia's offline host is still running, where the modal treats the encyclopedia as a place to join from. It leaves that session and runs the join again from the menu; a join the game would turn away anyway is not intercepted, and a waiting join is dropped when the player starts a game of their own, another join goes through, or the leave itself fails.
- the click lands while the game is running: `RichPresenceJoin` registers Steam's rich presence join callback, which the game never did, and hands the `+join_lobby` string to the same join the command line uses.
- the click lands while the game is closed: Steam starts the game with `+join_lobby <id>`, which the game's parser does not know (`-joinLobby` is the one it takes). `LaunchJoin` reads it once, then joins when Steam and the main menu are up, retrying if the request goes out too early.

Verbose logging narrates all of it: what a join asked for (`CliJoinTracePatch`, `JoinRequestTracePatch`), what it is connecting to (`JoinTracePatch`), when a rescue takes over, when a retry happens ("2 of 2"), and when one is given up on.

Testing a Steam join needs the Steam client: the rich presence path only happens through its own Join Game while the game runs, and the cold-launch path only happens when Steam starts the game with the argument.

## Limitations

- A server this machine can never query stays as whatever copy Steam hands over. If it never answered once, that copy's name is its address and its ping is blank. The marker says so.
- Every row a refresh delivers costs one probe of that server's query port, from every client that refreshes. That is the game's own fan-out, not the mod's; the card's Refresh adds one more, for one server.
- The join fixes act on the Steam paths. What the game does after a connect succeeds, disconnects included, is untouched.
- The lobby-data watchdog gives up after roughly sixteen seconds (two eight-second waits) with the game's own message; a lobby that comes back after that needs a new join.
- The card's Refresh button is a master list request, so it cannot reach further than a refresh can: a server whose query port does not answer fails it too, and the card then keeps what it had.

## Status

Version 0.3.0, played against game 0.34.6. The join paths were developed and verified in play against 0.34.1, a running game and a cold launch through the Steam client both included. The 0.34.6 browser work (unreachable listing, copy repair, markers, card refresh, full-server join) is built against the new binaries and needs a play test.

The code was split byte for byte out of Map Mouse Select 0.3.0, where the join half of it was developed.
