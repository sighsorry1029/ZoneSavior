| `Version` | `Update Notes`                                                                                   |
|-----------|--------------------------------------------------------------------------------------------------|
| 1.0.10    | - Allowed RCON and dedicated server zone loads/restores to use nearby ZoneSavior clients as terrain witnesses when the server has not loaded the target terrain. |
| 1.0.9     | - Removed admin terrain tool pieces from build tables when the player is not in the allowed admin/debug state. |
| 1.0.8     | - Added safer Expand World Data and TerrainMistile compatibility for ZoneSavior terrain proxies. <br> - Cleaned watcher reload handling, terrain reset placement, and zone bundle capture/load internals. |
| 1.0.7     | - Kept admin terrain proxy prefabs alive across scene/world reloads. <br> - Sanitized dead piece table references before recipe refreshes to prevent Jotunn/Unity null-reference errors. |
| 1.0.6     | - Added a safe text-input visibility fallback so the zone boundary overlay no longer logs repeated errors when another mod patches `TextInput.IsVisible` before its UI is ready. |
| 1.0.5     | - Fixed admin terrain tools on dedicated servers with ServerDevcommands. <br> - Reduced optional Infinity Hammer and WorldEditCommands compat noise. <br> - Registered terrain tool pieces with VeiledRecipes when available. |
| 1.0.4     | - Cleaned release packaging and synced package version from the DLL assembly version.             |
| 1.0.3     | - Added terrain proxies that can be saved in blueprints. <br> - Refactoring and config clean up. |
| 1.0.2     | - Fixed zs_loadarchive command not working on dedi                                               |
| 1.0.1     | - Fixed ZoneSavior commands not working for admins on dedi                                       |
| 1.0.0     | - Initial Release                                                                                |
