# Configuration

Every setting is in the file the mod writes, `BepInEx\config\com.kerbiter.multiplayerlobbyfixes.cfg`, and in the Configuration Manager window (F1) while the game runs.

| Section | Key | Default | Meaning |
|---|---|---|---|
| Server browser | `ListUnreachableServers` | `true` | List the dedicated servers whose query port does not answer this machine instead of dropping them. The row is built from the copy Steam's master list handed over: name, player count and password flag, plus the map and mission its Steam tags carry. Its ping stays blank and it keeps a warning triangle. Player hosted games are not affected |
| Server browser | `JoinFullServers` | `true` | Leave the Join button in the server details window clickable when the player count says the server is full. That count is what the server last registered with Steam, so it is often stale. A server that really is full refuses the join itself |
| Server browser | `ShowLatestQueryStatus` | `true` | Mark a row whose server is not answering: a warning triangle in the ping column when the master list could not reach it, and one in the mission cell when its rules query failed, or a sync icon while that query is still out. The same marks appear in the details window. A server with nothing wrong shows nothing |
| Server browser | `RefreshServerButton` | `true` | Add a Refresh button to the server details window, left of Join, which asks that one server for its rules again: the mission, the map, the description and the duration. The card is read again when the answer lands. The rest of the list is not touched |
| Server browser | `RefreshCooldownSeconds` | `3` | How long one server's rules are left alone after the card's Refresh button asked for them, so a held down button cannot spend a query a frame on the same server. Range 0 to 60 |
| Diagnostics | `VerboseLogging` | `true` | Log what the browser and the Steam joins are doing |
