<p align="center">
  <img src="docs/icon.png" width="96" alt="SoundDeck icon">
</p>

<h1 align="center">SoundDeck</h1>

<p align="center">
  A free soundboard for Windows that plays sounds <b>through your microphone</b> into Discord, games, OBS and other voice apps.
  It has global hotkeys, categories and built-in recording.
</p>

<p align="center">
  <a href="../../releases/latest"><b>⬇ Download the latest version</b></a>
</p>

![SoundDeck main window](docs/screenshot-main.png)

## Features

- **Plays into your mic.** People in voice chat hear your voice and your sounds together. SoundDeck mixes your real microphone in.
- **One-click virtual mic setup.** SoundDeck downloads and installs the free [VB-CABLE](https://vb-cable.com) driver for you, after checking that it's signed by VB-Audio.
- **Global hotkeys.** Play any sound with a key, even while a game has focus. There's also a hotkey that stops everything.
- **Categories and favorites.** Sort sounds into tabs, star your favorites, and switch tabs with Ctrl+1–9. Adding a folder turns it into a category.
- **Drag to reorder.** Move pads around, or drop one onto a tab to change its category.
- **Record sounds.** Record from your microphone or from your PC's audio (for example a YouTube clip or a game sound). Silence is trimmed automatically, and you can fine-tune the trim on the waveform.
- **Per-sound volume** (0–200%), so loud and quiet clips sit at the same level.
- **Hear it yourself.** Optionally play sounds on your own headphones too, with a separate volume.
- **Search** across all sounds, and a **compact view** for big boards.
- **Supported formats:** MP3, WAV, M4A/AAC, WMA and FLAC.
- **Small and portable.** It's a single ~140 KB `.exe` that needs no installation, and nothing runs in the background when you close it.

<p align="center">
  <img src="docs/screenshot-compact.png" width="49%" alt="Compact view">
  <img src="docs/screenshot-record.png" width="44%" alt="Recording a sound">
</p>

## Getting started

1. [Download the latest release](../../releases/latest), unzip it somewhere permanent (for example Documents), and run `SoundDeck.exe`.
   > Windows may say *"Windows protected your PC"* because the app isn't code-signed. If it does, click **More info → Run anyway**.
2. In the sidebar, click **Install virtual mic**. You only need to do this once. Approve the Windows prompt, click **Install Driver**, and restart your PC if you're asked to.
3. In Discord, go to **Settings → Voice & Video** and set **Input Device** to **CABLE Output (VB-Audio Virtual Cable)**.
4. Keep **Mix my microphone in** turned on in SoundDeck, so people still hear you.

For the best sound in Discord, also set **Noise Suppression** to *None*, and turn off **Echo Cancellation** and **Automatic Gain Control**. Discord's voice filters treat music and sound effects as noise.

## Using it

| | |
|---|---|
| **Add sounds** | Click **Add sounds**, or drag files or folders onto the window |
| **Play / stop** | Click a pad, then click it again to stop |
| **Hotkeys, volume, category, rename** | Right-click a pad |
| **Reorder** | Drag a pad. To change its category, drop it on a tab |
| **Favorites** | Click the ☆ on a pad |
| **Record** | **Record** button or **Ctrl+R** |
| **Search** | **Ctrl+F**, then **Enter** plays the first match |
| **Stop everything** | **Stop all**, **Esc**, or your stop-all hotkey |

Your board and settings are saved in `%APPDATA%\SoundDeck`. Recordings are saved as WAV files in `Documents\SoundDeck Recordings`.

> **Note:** SoundDeck has to be running for your voice to reach Discord while its input is set to *CABLE Output*. When you're done, switch Discord back to your normal microphone.

## How it works

```
 your mic ──┐
            ├─► SoundDeck mixer ──► CABLE Input ══ VB-CABLE ══► CABLE Output ──► Discord / game / OBS
 sounds ────┘          │
                       └──► your headphones (optional)
```

Windows can't create a microphone without a signed audio driver, so SoundDeck uses VB-CABLE as its virtual mic. Sounds are decoded with Windows Media Foundation and resampled to 48 kHz with a windowed-sinc filter. They're mixed with your microphone, run through a soft limiter so overlapping sounds don't distort, and sent to the cable. Recording your PC's audio uses WASAPI loopback.

## Building from source

There's no SDK or Visual Studio to install. SoundDeck builds with the C# compiler that ships with Windows (.NET Framework 4.x):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1      # builds SoundDeck.exe
powershell -ExecutionPolicy Bypass -File package.ps1    # builds dist\SoundDeck-<version>.zip
```

The code is in [`src/`](src). To test without touching your own board, set `SOUNDDECK_DATA` to another folder. That copy uses its own board and settings, and can run next to your normal one.

## Credits

The virtual microphone is **VB-CABLE** by VB-Audio Software ([vb-cable.com](https://vb-cable.com)). VB-CABLE is donationware: if you find it useful, please consider supporting its author. SoundDeck is not affiliated with VB-Audio.

## License

[MIT](LICENSE)
