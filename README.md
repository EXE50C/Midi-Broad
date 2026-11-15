# Midi Broad (MBD)

Turn a spare USB keyboard into a MIDI controller on Windows.

Midi Broad captures the keys you choose (for example `Z S X D C …`), maps them to MIDI notes, and exposes a MIDI port named **Midi Broad** that any DAW or MIDI monitor can open.

> Status: v0.1 prototype. The Windows MIDI Services API is still a preview. The app now uses a service-owned loopback endpoint pair instead of an app-owned virtual device, so closing the app no longer tears down the MIDI transport.

---

## Requirements

- Windows 11 **25H2 or newer** (build 26200+). Windows MIDI Services is part of the OS on these builds.
- .NET 10 SDK to build. No Visual Studio is required.
- A physical USB/HID keyboard. PS/2 and on-screen keyboards are intentionally excluded.
- **Run from a normal-integrity folder.** A folder carrying the NTFS `Low Mandatory Level` label makes the process low-integrity; Windows then blocks global key capture while another normal app is focused. Extract the ZIP to a normal folder such as `C:\Apps\MidiBroad`. The app warns in its status area if it detects low integrity.

The MIDI SDK package is checked in under `packages/` so the build is offline-friendly:

```
packages/Windows.Devices.Midi2.0.99.92-preview.11.nupkg
```

## Build

```powershell
dotnet build ".\MidiBroad.sln" -c Debug -p:Platform=x64
```

The app is produced at:

```
src\MidiBroad.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\MidiBroad.exe
```

Run it with:

```powershell
dotnet run --project .\src\MidiBroad.App\MidiBroad.App.csproj -c Debug -p:Platform=x64
```

## Use

1. Start `MidiBroad.exe` from the portable folder.
2. Pick the physical keyboard in **Input device**, press **Start listening**.
3. In the DAW, open **Midi Broad** as a MIDI input.
4. Play `Z S X D C …` / `Q 2 W 3 E …`.
5. Use **Edit keys** in the piano panel to reassign notes; click **Done** to save.
6. Close the window with **X** to exit the process. On the next launch Midi Broad checks the Windows MIDI service; if it cannot create the virtual port, it asks for administrator permission and restarts `midisrv` automatically, then retries once.

If keys do not respond, switch the active IME to English/direct input and make sure the window is in **LISTENING** state.

### Pedals

Four latching pedals are mapped to **F1–F4** and shown above the piano:

| Key | Pedal | MIDI CC |
|---|---|---|
| F1 | Soft | 67 |
| F2 | Sostenuto | 66 |
| F3 | Sustain | 64 |
| F4 | Legato | 68 |

Press once to turn a pedal on and again to turn it off. Enabled pedals are bright; disabled pedals are dimmed. The same indicators can also be clicked with the mouse.

### Config file

All shareable settings live in one JSON file next to `MidiBroad.exe`:

```
dist\MidiBroad\config.json
```

It contains key mappings, the virtual MIDI device name, selected input device, theme/accent/background and behavior settings. Saves go there by default; if the folder is read-only, `%APPDATA%\MidiBroad\config.json` is used instead.

Open **Settings → CONFIG FILE** to see the active path, **Export…** a copy to share, or **Import…** a config from another machine. Old `settings.json` files are read once and migrated automatically.

### Chord table

Chord recognition reads `chords.json` from the folder next to `MidiBroad.exe`. Edit that file and restart the app — no rebuild is needed. Each entry is a suffix and its semitone intervals from the root:

```json
{ "suffix": "maj7", "intervals": [0, 4, 7, 11] }
```

The lowest pressed note is the root. Intervals are compared by pitch class, so a 9th can be `14` (as in the supplied table) or `2`. If the file is missing or invalid, the built-in table is used. The chord index is editable in both the source tree (`src\MidiBroad.App\chords.json`) and the published folder.

### Language packs

English and Simplified Chinese ship in `lang/en.json` and `lang/zh-CN.json`. Choose the language in **Settings → Language**; the current windows switch immediately. A pack is a flat JSON object mapping English source text to the translated text, so adding another language only requires a new file and an entry in `LanguageService.Available`.

### Self-test

`--selftest` starts the app, creates the virtual MIDI port, waits a few seconds, sends C/E/G Note On/Off, then exits:

```powershell
dotnet .\src\MidiBroad.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\MidiBroad.dll --selftest
```

### UI-only preview

`--ui-only` skips Windows MIDI Services so the interface can be inspected on a machine where the MIDI service is unavailable. Add `--listen` to arm the keyboard hook without a MIDI output; useful while developing the mapping layer.

### Publish

Self-contained portable folder (recommended; no .NET installation needed):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-portable.ps1
```

Output: `dist\MidiBroad\` — keep the folder together and run `MidiBroad.exe` from it. The publish script also creates a normal-integrity copy in the sibling `MidiBroad` folder (for example `F:\MidiBroad`) and a shareable `dist\MidiBroad.zip`. Run from the sibling/extracted copy, not from a folder carrying the NTFS Low integrity label.

Single-file publishing is intentionally not used. The Windows MIDI Services native DLL/`.pri` and .NET's bundle extraction create failures on machines with locked-down or redirected temp directories.

## Current features

- Keyboard picker with hot-plug refresh and per-device identity.
- Soft capture of the mapped keys through a global low-level keyboard hook.
- Fixed, non-configurable release hotkey: **Ctrl + Alt + Shift + F12**.
- Virtual MIDI endpoint named **Midi Broad** through Windows MIDI Services.
- Mapping presets: Piano (25 keys, two rows), Chromatic, GM Drums.
- Octave shift, transpose, MIDI channel, fixed/random velocity with humanize.
- Pitch bend key with configurable range and direction.
- Bottom piano keyboard with active-note highlighting; mouse velocity follows click height (top = soft, bottom = hard).
- **Chord display**: holding one mapped key shows its note name; holding several keys shows the recognized chord, its scale degree in the current key (derived from Transpose), and the chord tones. Supported shapes include triads, sevenths, sixths, ninths and 7sus4 from the supplied chord table.
- **Edit keys**: edit the physical key assigned to each piano note in place, persisted in `config.json`.
- Performance keys: hold Left Shift = octave up, hold Right Shift = octave down (releasing restores the base octave), ←/→ = pitch bend, ↑/↓ = velocity.
- Four latching pedals on **F1–F4** (soft, sostenuto, sustain, legato) with clickable colour indicators above the piano.
- Light/dark/system theme, accent colours, custom background image.
- Chinese/English UI with JSON language packs in `lang/`; switch live in **Settings → Language**.
- Portable, shareable `config.json` with export/import from Settings.
- Per-user auto-start.
- Panic button (All Notes Off + pitch bend centre).

## Soft capture — what it does and does not do

Windows does not allow a normal application to open one PnP keyboard exclusively. The Win32 input subsystem owns all keyboards, and Raw Input can identify a device but cannot stop other applications from receiving it.

v0.1 therefore uses **soft capture**:

- Mapped keys are swallowed globally while listening and converted to MIDI.
- Unmapped keys still reach the focused application.
- The fixed hotkey always releases capture.
- Other keyboards pressing a mapped key will also produce MIDI. This is a limitation of doing this without a kernel filter driver.

True per-device isolation needs an upper keyboard filter driver (for example Interception) or a custom driver. That is deliberately left for a later version; see `docs/feasibility-and-architecture.md`.

## Known issues

### Windows MIDI Services endpoint lifecycle

Midi Broad creates a **transient loopback endpoint pair** owned by Windows MIDI Services. The public side (`Midi Broad`) is what the DAW opens; the internal side (`Midi Broad (internal)`) is where the app sends. The pair is not tied to the app process, so closing with **X** no longer tears down a virtual device or wedges the service. On the next launch the app reuses the same pair.

If the service is genuinely unresponsive, the app still offers a UAC-assisted `Restart-Service midisrv -Force` retry as a last resort. A service restart removes transient loopbacks; the next launch recreates them.

### Background key capture and folder integrity

`WH_KEYBOARD_LL` events are blocked for low-integrity processes when a normal-integrity application has focus. Some development/sandbox folders carry an NTFS `Low Mandatory Level` label; running `MidiBroad.exe` from such a folder makes capture work only while the Midi Broad window is focused. Check with:

```powershell
icacls .\MidiBroad.exe
```

If `Mandatory Label\Low Mandatory Level` appears, extract/copy the app to a normal folder (for example `C:\Apps\MidiBroad`) and run it there. This is an OS integrity boundary, not a hook bug.

### Windows MIDI Services preview API

The SDK used here is `Windows.Devices.Midi2 0.99.92-preview.11`. The API is locked but still ships out-of-band. Keep the package and the Windows build in sync.

### Input methods (IME)

Some Chinese/Japanese/Korean input methods can still consume keystrokes before a global low-level keyboard hook sees them. If physical keys do not light up the piano while **Start listening** is active, switch the active input method to **English / direct input** and try again. Midi Broad disables IME composition for its own windows, but it cannot disable the IME of the application you are typing into.

The earlier “nothing happens” case was caused by the app registering the keyboard for Raw Input in the same process. That registration suppressed `WH_KEYBOARD_LL` callbacks; it has been removed.

### Keyboard hardware

Cheap keyboards have limited anti-ghosting. Chords may drop keys; this is a hardware limitation and is not caused by Midi Broad.

## Project layout

```
MidiBroad.sln
src/MidiBroad.App/
  Core/          settings, theme, mapping engine, logging
  Input/         Raw Input enumeration, soft keyboard capture
  Midi/          Windows MIDI Services output
  Models/        settings, devices, presets, MIDI messages
  Controls/      piano keyboard
  Themes/        light/dark resource dictionaries
docs/            design and feasibility notes
packages/        offline NuGet package
tools/           icon generator
```

## License

MIT. See `LICENSE`. The Windows MIDI Services SDK package is MIT-licensed; Interception is not used in this version.

