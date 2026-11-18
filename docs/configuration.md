# Configuration

Every setting is in the file the mod writes, `BepInEx\config\com.kerbiter.multiplayerlobbyfixes.cfg`, and in the Configuration Manager window (F1) while the game runs.

| Section | Key | Default | Meaning |
|---|---|---|---|
| Server browser | `ListUnreachableServers` | `true` | List the dedicated servers whose query port does not answer this machine instead of dropping them. Each one keeps whatever it last said about itself: name, player counts, password flag, map, mission, description and ping. Only the ping of a server this machine never measured stays blank. The row gains a warning triangle, which comes off as soon as the server answers anything. Player hosted games are not affected |
| Server browser | `JoinFullServers` | `true` | Leave the Join button in the server details window clickable when the player count says the server is full. That count is what the server last registered with Steam, so it is often stale. A server that really is full refuses the join itself |
| Server browser | `ShowLatestQueryStatus` | `true` | Mark a row that is not up to date: a sync icon while Steam's master list is being asked about that one server, a warning triangle when the master list could not reach it, and one in the mission cell when its rules query failed or a sync icon while that query is out. The same marks appear in the details window. A server with nothing wrong shows nothing |
| Server browser | `RefreshServerButton` | `true` | Add a Refresh button to the server details window, left of Join, which asks Steam's master list about that one server: name, player counts, ping, map, mission, description, start time and password flag. One request for one server instead of a whole list refresh, and no other row is touched. The button greys out for the same time as the browser's own Refresh button |
| Server browser | `RefreshCooldownSeconds` | `3` | How long the details window's Refresh button stays grey after a press, so a held down button cannot spend a request a frame on the same server. It is grey while the browser's own Refresh button is grey too, whatever this says. Range 0 to 60 |
| Diagnostics | `VerboseLogging` | `true` | Log what the browser and the Steam joins are doing |
