# TeamCity Tray

A small Windows tray app that shows your running and queued TeamCity builds in a compact always-on-top widget. It plays a short sound when a build finishes: one sound for success, a different one for failure.

> Screenshot: _placeholder, add `docs/screenshot.png` here._

## Features

- Tray icon (generated in code) with Show/Hide widget, Settings, Mute sounds and Quit. Single instance.
- Borderless dark widget docked bottom-right, draggable, position remembered. One row per build: status dot, build type, `#number`, progress bar, percent and elapsed time. Queued builds show `queued`. Child builds are indented under their root. Click a row to open the build in your browser.
- Optional auto-hide when nothing runs. A small red `offline` line shows when the server is unreachable.
- Sounds are synthesized at startup. Custom `.wav` files can replace them. Several builds finishing in the same poll play one sound, and failure wins.

## Setup

1. In TeamCity, open your profile and go to **Access Tokens**. Create a token.
2. Start the app. The Settings window opens on first run.
3. Enter the server URL and the token. Press **Test connection**: it shows your resolved username.
4. Save.

Settings live in `%APPDATA%\TeamCityTray\settings.json`. The token is encrypted with DPAPI (current Windows user) and is never logged.

## Build

Requires the .NET 9 SDK.

```
dotnet build -c Release
dotnet publish src/TeamCityTray -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## How the "mine" filter works

With **Only my builds** enabled, a build is shown when one of these holds:

1. **Trigger user.** `triggered.user.username` equals your username (case-insensitive). This is a root build.
2. **Build relation.** It has a snapshot-dependency relation with one of your builds (either direction), or it was triggered by one of your builds (`triggered.build.id`).
3. **Same run number** (option **Link children by build number**). Some setups queue child builds through a bot account, so children have no REST link to the parent. They do share the root's project and build number, and they are queued after it. A build with the same `projectId` and `number` as one of your roots, queued at or after the root, is treated as its child.

The rules repeat until nothing new is found, so grandchildren are caught. Known roots are remembered for 6 hours, because a root can finish long before its children do.

Builds that leave the running list are queried once for their final status. `SUCCESS` plays the success sound. Any other status, including canceled builds, plays the failure sound.

## License

MIT
