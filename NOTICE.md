# Notices

Stremio-XBOX is licensed under the GNU General Public License v2.0 (see `LICENSE`).

## Jellyfin for Xbox

Parts of this project are adapted from [Jellyfin for Xbox](https://github.com/jellyfin/jellyfin-xbox)
(GPL-2.0), as of commit `14aaa6dd00c71ae3b31bdd7bcb8231b2c0dbee64`:

- `StremioXbox/Core/DisplayModeManager.cs` is adapted from `Jellyfin/Core/FullScreenManager.cs`
  (HDMI display mode and HDR option selection).
- The WebView2 hosting approach (script injected with `AddScriptToExecuteOnDocumentCreatedAsync`,
  messages over `postMessage`), the `hevcPlayback` and `privateNetworkClientServer` capabilities,
  and the layout-scaling and bounds-mode settings follow that project.

## Stremio

This project is not affiliated with or endorsed by Stremio. It loads the public Stremio Web app
(https://web.stremio.com). "Stremio" is a trademark of its owner. Xbox is a trademark of Microsoft.
