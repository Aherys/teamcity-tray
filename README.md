# TeamCity Tray

A small Windows tray app that shows your running and queued TeamCity builds in a compact always-on-top widget. It plays a short sound when a build finishes: one sound for success, a different one for failure.

> Screenshot: _placeholder, add `docs/screenshot.png` here._

## Features

- Tray icon (generated in code) with Show/Hide widget, Open TeamCity, Dismiss all, Settings, Mute sounds, Always on top, Hide when idle, Start with Windows, Reset position and Quit. Single instance.
- Borderless dark widget docked bottom-right, draggable by a grip strip at the top or by any row, position remembered (clamped to the visible screens on load; **Reset position** in the tray menu). One row per build: status dot, build type, `#number`, progress bar, percent and elapsed time. Queued builds show `queued`. Child builds are indented under their root. Click a row to open the build in your browser. Hover buttons: open, re-run, dismiss.
- Optional auto-hide when nothing runs. Failed builds stay visible until dismissed. A small red `offline` line shows when the server is unreachable.
- Sounds are synthesized at startup. Custom `.wav` files can replace them. Several builds finishing in the same poll play one sound, and failure wins.

## Setup

1. In TeamCity, open your profile and go to **Access Tokens**. Create a token.
2. Start the app. The Settings window opens on first run.
3. Enter the server URL and the token. Press **Test connection**: it shows your resolved username.
4. Save.

Settings live in `%APPDATA%\TeamCityTray\settings.json`. The token is encrypted with DPAPI (current Windows user) and is never logged.

## Install

Run `./install.ps1` from the repo root. It publishes to `%LOCALAPPDATA%\TeamCityTray`, stops any running instance, and starts the new exe. The app registers itself to start with Windows on first run (`HKCU\...\Run`, value `TeamCityTray`). The registered path follows the current exe at each start.

- **Update:** run `./install.ps1` again.
- **Uninstall:** untick **Start with Windows** (Settings or tray menu), quit the app, and delete `%LOCALAPPDATA%\TeamCityTray`.

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
3. **Child link.** Some setups queue child builds through a bot account, so children have no REST link to the parent. A build that you did not trigger is a child of one of your roots when it has the same `projectId`, was queued at or after the root, and one of these holds:
   - **Same number** (option **Link children by build number**): same build `number` as the root.
   - **Same comment**: same non-empty comment text as the root.
   - **Bot account window**: the build was triggered by a user listed in **Bot accounts** (comma-separated, empty by default) and was queued between the root start and 120 s after the root finish, or the root still runs.

A child goes under the most recent root that matches. The rules repeat until nothing new is found, so grandchildren are caught.

Each poll also reads your builds of the last 6 hours from the server. A root that finished before the app started, or before the children appeared, is still found. If the server rejects this query, the app falls back to the roots it saw while they were active.

A group shows its root at the top and its children indented. A finished root or child stays in the group as a row with a check mark or a cross, while any build of the group is queued or running. Then it stays for **Keep finished builds** seconds.

## Failures

- A build that finishes with a status other than `SUCCESS` (canceled builds included) plays the failure sound and stays on the widget until you dismiss it.
- The widget stays visible while a failed row exists, even with **Hide widget when nothing runs**.
- On a failure, the taskbar button flashes, the widget comes to the front and a red border pulses for 5 seconds. Successes do not flash.
- Dismiss one row with its cross button, or use **Dismiss all** in the tray menu.

Widget visibility: shown when a build is queued or running, or a failed row exists, or **Hide widget when nothing runs** is off.

## Row buttons

Hover a row to see its buttons. Drag still works on the grip and on the row.

- **Open** (arrow): opens the build in your browser (http and https only). Clicking the row does the same. The tray menu has **Open TeamCity** for the server page.
- **Re-run** (circular arrow, finished rows): asks for confirmation, then queues the same build type with the same branch and custom parameters. The comment starts with `Re-run of #<number>`. The new build is triggered by you, so it is a new root and appears on the next poll. An error shows the HTTP status.
- **Dismiss** (cross, failed rows): hides the row.

Builds that leave the running list are queried once for their final status. `SUCCESS` plays the success sound. Any other status plays the failure sound.

## License

MIT
