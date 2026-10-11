<div align="center">

# Stremio-XBOX

### Stremio on Xbox, the way your TV and soundbar were meant to play it.

**4K · HDR10 · Dolby Vision · Dolby Atmos · 100+ GB remuxes without a stutter**

[![Xbox Series X|S](https://img.shields.io/badge/Xbox-Series%20X%7CS-107C10?style=for-the-badge&logo=xbox&logoColor=white)](#-install)
[![Dev Mode](https://img.shields.io/badge/Dev%20Mode-UWP-5C2D91?style=for-the-badge)](#-install)
[![Dolby Vision](https://img.shields.io/badge/Dolby%20Vision-P5-000000?style=for-the-badge&logo=dolby&logoColor=white)](#-features)
[![Dolby Atmos](https://img.shields.io/badge/Dolby-Atmos-000000?style=for-the-badge&logo=dolby&logoColor=white)](#-features)
[![License](https://img.shields.io/badge/License-GPL--2.0-blue?style=for-the-badge)](LICENSE)

[Features](#-features) · [Controls](#-controls) · [Install](#-install) · [Limitations](#%EF%B8%8F-known-limitations)

**No streaming server. No PC. Just the XBOX.**

</div>

---

Stremio Web in a controller-first Xbox app. Direct streams (debrid links like TorBox via AIOStreams)
skip the browser player and go straight to the Xbox's own media pipeline: real HDR, real Atmos, and
huge files that just play.

> Unofficial. Not affiliated with or endorsed by Stremio.

<div align="center">

## ✨ Features

</div>

<table>
<tr>
<td width="50%" valign="top">

**🎬 Native player**<br>
Hardware decoding through the Xbox's own media pipeline. 4K at the file's real frame rate.

</td>
<td width="50%" valign="top">

**🌈 HDR10 & Dolby Vision (Profile 5)**<br>
The TV switches into the right mode per file, automatically. DoVi 7/8 play as HDR10.

</td>
</tr>
<tr>
<td width="50%" valign="top">

**🔊 Dolby Atmos, DD+, AC3**<br>
Passed through untouched to your soundbar or AVR.

</td>
<td width="50%" valign="top">

**🚀 Custom read buffer**<br>
6 parallel connections and up to 768 MB of RAM read-ahead that follows the playhead. Built for remuxes that make other players choke.

</td>
</tr>
<tr>
<td width="50%" valign="top">

**💬 Subtitles & audio tracks**<br>
Embedded and addon subtitles. Switch language or track mid-play.

</td>
<td width="50%" valign="top">

**🎮 Made for the controller**<br>
Full D-pad navigation, B to go back, a built-in cursor for the hard-to-reach bits, and the Xbox keyboard for sign-in and search.

</td>
</tr>
</table>

**Formats:** MKV, MP4 · HEVC, H.264 · E-AC3 (Atmos), AC3, AAC and more.

**🔥 How the read buffer works**

Windows' media player reads a network stream one request at a time, and on large MKVs it keeps
jumping to a position far ahead of playback that the download can't keep up with. Every wait
freezes the picture. Stremio-XBOX puts its own cache in between:

- 6 parallel HTTP range connections in 4 MB chunks
- up to 768 MB of read-ahead in RAM, anchored on where playback actually is
- a quick seek right after playback starts that stops the player's far-ahead reads, which was the
  real cause of stutter on 80+ GB files


## ⚡ Why not just use Edge?

| | Edge / web player | **Stremio-XBOX** |
| --- | :---: | :---: |
| MKV · HEVC (x265) | ❌ "Stream is not supported" | ✅ Plays natively |
| 4K HDR10 · Dolby Vision | ❌ No mode switching | ✅ Auto resolution, refresh rate, HDR |
| Dolby Atmos · DD+ | ❌ Downmixed or silent | ✅ Bitstream passthrough |
| 100+ GB remuxes | ❌ Buffering, stutter | ✅ Smooth |
| Controller | ❌ Mouse-mode cursor | ✅ D-pad, back, cursor, keyboard |

## ⚠️ Known limitations

- **Dolby Vision Profile 7 / 8 play as HDR10.** The Xbox only outputs DV for Profile 5.
- **Dolby TrueHD sometimes has no sound.** Pick an E-AC3 / AC3 track if that happens.
- Built for direct / debrid links. Torrent streams need a streaming server.

## 🚧 Work in progress (WIP)

- Switching audio **formats** mid-play (e.g. E-AC3 → AC3) can crash the player. Same-format switches are fine.
- Seeking far past the buffered area can freeze for 5–20 s while the server catches up.
- Native player progress is not synced to Stremio's Continue Watching yet.

## 🎮 Controls

<table>
<tr>
<td valign="top">

**Browsing**

| Button | Action |
| --- | --- |
| D-pad / Left stick | Move |
| A | Select |
| B | Back |
| Left stick click | Cursor mode |
| View | Debug log |

<sub>Cursor mode: stick moves, A clicks, right stick scrolls.</sub>

</td>
<td valign="top">

**Player**

| Button | Action |
| --- | --- |
| A / Menu | Play / Pause |
| ◀ / ▶ | −10 s / +10 s |
| LB / RB | −60 s / +60 s |
| Y | Audio track |
| X | Subtitles |
| B | Stop |

</td>
</tr>
</table>

## 📦 Install

1. Put your Xbox in Dev Mode and learn the Device Portal install:
   **[How to activate Dev Mode and install UWP apps](https://www.reddit.com/r/XboxRetailHomebrew/comments/1ktgw97/guide_how_to_activate_dev_mode_and_install_uwp/)**
2. Download the latest zip from **[Releases](../../releases)**.
3. Device Portal → **Add** → pick the `.msix`, add every file from `Dependencies`, install.
4. Launch **Stremio-XBOX**, sign in, add your addons as usual.

<details>
<summary><b>Build it yourself (Windows)</b></summary>

<br>

Visual Studio 2022 with the UWP workload and Windows SDK 10.0.22621.

1. `powershell -ExecutionPolicy Bypass -File scripts\New-DevCert.ps1` (once, creates the signing cert)
2. Open `StremioXbox.sln`, **Release | x64**.
3. Deploy to the Xbox as a Remote Machine, or Publish → Create App Packages → Sideloading and install
   through Device Portal.

</details>

## Credits

- [Jellyfin for Xbox](https://github.com/jellyfin/jellyfin-xbox): display mode switching and the
  WebView2 hosting approach.
- [Stremio](https://www.stremio.com): the app this wraps.
- [Claude](https://claude.ai): helped build this.

<div align="center">
<sub>GPL-2.0 · see <a href="LICENSE">LICENSE</a> and <a href="NOTICE.md">NOTICE.md</a></sub>
</div>
