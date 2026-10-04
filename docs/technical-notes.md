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

## The server browser

The failure this all grows from is Steam's own: every refresh asks the master list for servers and then asks Steam to probe each one, and that probing stops answering after the first refresh of a session, for all servers or most. Vanilla answers that with a shrinking list. The mod keeps its own merged view instead: for each server, the master list's copy plus the last rules answer (`ServerBrowser.LastRules`), the last real name seen (`LastNames`), and the last ping this machine measured (`LastPings`, `PingsMeasured`).

`ServerListFailurePatch` postfixes the request's `OnServerFailedToRespond`. A direct join's server is rescued there (below). For the browser, a failed probe becomes an entry built by hand from the master list's copy, which already carries the name, the player count and the password flag; running vanilla's rules query instead would spend a query on a server that has just shown it does not answer. Listing one costs no queries at all beyond the master list request that was already made. The details of a server already known are repaired on the way in by `ServerDetailsPatch`, so its name, counts and password survive the copy being rewritten, and a name patch keeps the last real name, since Steam hands a server it cannot reach back with its `ip:port` where the name was.

The rules side gets the same treatment: a rules query that fails (`SearchQueryFailurePatch`) lists the server anyway, skipping the hide-full and hide-empty checks, because the values they read are nought exactly for the servers this is for, and the password flag the master list carries is honoured. A rules answer (`SearchQueryAnswerPatch`) is stored, and a refresh that brings no answers refills the stored rules into the rebuilt entry rather than losing the mission and the map. The ping loop's answers do the same for the name, player count and password flag, so rows keep moving while the master list is failing.

Keeping the rows is a set of patches around the list's own clearing: refreshes merge into the rows on screen instead of emptying them, the lobby cache survives leaving the multiplayer screen and coming back (`SteamLobby.ClearLobbyCache` and the `LobbyList` start path), and only a filter change clears the list. `LocalFilters` answers `LobbyList.GetListOfLobbies` from the servers already in hand, so a toggle costs no queries; the hold-out of filtered servers from the ping loop rides along. `FullServerJoinPatch` leaves the details window's Join button clickable when the count reads full, and `LobbyRefreshButtonPatch` adds the details window's own Refresh button.

Queries are made countable. `QueryThrottle` queues the browser's per-server rules queries and pings (`SteamMatchmakingServers.ServerRules` and `PingServer` prefixes) and drains at most one a frame, no sooner than `QuerySpacingMs` apart, so a refresh trickles to Steam instead of arriving in one burst; a query over the budget waits rather than being dropped, and a details-window Refresh press bypasses the queue. `ThrottleQueries` turns the queue off outright, everything going out at once. A press of the list's Refresh button re-asks every server the browser holds for its rules and its status through that queue (`ServerBrowser.RefreshListed`), skipping the ones asked within the last three seconds, because Steam's own refresh queries almost nothing on every press after the first. `PingHoldPatch` pushes the ping countdown of the unreachable servers 30 seconds forward on every `CheckPingServers` pass: a silent server never counts a ping up, so its countdown stays due and vanilla asks it again at the frame rate, one held socket per attempt, until the client's socket set runs out and the refreshes that matter come back nearly empty. The mark comes off the moment one of them answers anything.

`VerboseLogging` sits on the same budget: every line BepInEx writes reaches the console and the log file on the main thread, and the old line-per-query narration made a refresh press cost close to a second. The asks, the settle waves and the give-ups are one line per pass now; the queue and the marker transitions narrate nothing.

`StaleRows` draws the query state on the rows: a pulsing icon while a query is out, a warning triangle with a tooltip once the last one came back empty, on the ping column and the mission cell, and in the details window's own rows. A response that carried nothing real counts as empty: nought, a negative or Steam's 2000 placeholder where a ping should be, and a rules response that brought no mission and no map. A copy with no room in it is read as missing the same way, so its counts and password do not write over what is known.

## Steam joins

A join by Steam ID runs the game's `CliJoinHandler`: ask the master list for that one server, let Steam probe it, then let a rules query answer, and only then connect. Each step can fail on its own, and each failure has a patch:

- the probe fails (the common case): the join is rescued off the master list's copy, which carries the Steam ID the join connects to and the tags its version and password checks read. A password-protected server still asks its rules first, because the password check lives there, and one that never answers gives up after ten seconds. This is `ServerListFailurePatch` with its per-frame `TickRescue`.
- the rules query fails: `RulesFailureJoinRescuePatch` skips vanilla's "could not be found" and joins off the copy, version and password included.
- the refresh finds nothing: `JoinRetryPatch` asks the master list once more before the game's verdict stands, which is what a player retrying by hand does.
- the lobby's data never arrives: `SteamJoinWatchPatch` watches the pending join, re-requests the data after eight seconds, and after two attempts gives up with the game's own "Lobby no longer exists or Steam is unavailable" message instead of waiting forever.
- the join is refused: `JoinFailurePatch` shows the reason the game's own code produces ("Server full" and the rest) outside the server browser too, where vanilla keeps it silent, and consumes the reason the game armed for its main menu so one failure cannot be announced twice. It only speaks for joins it saw start and never connect: a disconnect from a session that did connect keeps vanilla's handling, a stop the local side caused (a quit, a leave for another join) is not a refusal and stays silent too, and a reason that was already pending is not consumed, so it still reaches the menu.
- the click lands while a game is already running: the join goes through the same `InviteJoinModal` the friends-list invites use, which asks before leaving ("Leave current game and join friend?", with the kick count when hosting others) and stops the network before the join. `LeaveThenJoinPatch` backs up the one state the modal skips its leave for: a join that reaches `TryJoinLobby` while the encyclopedia's offline host is still running, where the modal treats the encyclopedia as a place to join from. It leaves that session and runs the join again from the menu; a join the game would turn away anyway is not intercepted, and a waiting join is dropped when the player starts a game of their own, another join goes through, or the leave itself fails.
- the click lands while the game is running: `RichPresenceJoin` registers Steam's rich presence join callback, which the game never did, and hands the `+join_lobby` string to the same join the command line uses.
- the click lands while the game is closed: Steam starts the game with `+join_lobby <id>`, which the game's parser does not know (`-joinLobby` is the one it takes). `LaunchJoin` reads it once, then joins when Steam and the main menu are up, retrying if the request goes out too early.

Verbose logging narrates all of it: what a join asked for (`CliJoinTracePatch`, `JoinRequestTracePatch`), what it is connecting to (`JoinTracePatch`), when a rescue takes over, when a retry happens ("2 of 2"), and when one is given up on.

Testing a Steam join needs the Steam client: the rich presence path only happens through its own Join Game while the game runs, and the cold-launch path only happens when Steam starts the game with the argument.

## Limitations

- The servers listed from the master list that never answer stay blank by design; their ping column keeps the warning triangle until a query actually answers for them.
- The query spacing is a cost on big refreshes: N held servers take at least N x `QuerySpacingMs` to ask. At the 5 ms default a hundred-server refresh spends a second asking.
- The join fixes act on the Steam paths. What the game does after a connect succeeds, disconnects included, is untouched.
- The lobby-data watchdog gives up after roughly sixteen seconds (two eight-second waits) with the game's own message; a lobby that comes back after that needs a new join.
- A whole-list refresh can still fill the Steam client's socket table: it asks every server in hand, and a silent server's pair of queries holds its sockets until they time out. With a hundred silent servers the table fills and the console spams `count < FD_SETSIZE`. The hold covers only the servers the game itself failed on; the rest are asked again on every press. Under investigation.

## Status

Version 0.1.0, played against game 0.34.1. The browser changes and the join paths were developed and verified in play, a running game and a cold launch through the Steam client both included.

The code was split byte for byte out of Map Mouse Select 0.3.0, where all of it was developed, so the files keep that mod's internal namespace `MapMouseSelect`, and three GameObject names carry it too (`MapMouseSelectQueryThrottle`, `MapMouseSelectRefreshServer`, `MapMouseSelectStaleTooltip`). Both are cosmetic leftovers of the split, free to rename in a later pass.
