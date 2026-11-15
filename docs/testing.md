# Testing notes

## Verified

| Area | Result |
|---|---|
| Build (`dotnet build`, .NET 10, x64) | Passes with 0 warnings/errors |
| WPF main window | Renders; `artifacts/main-final.png` |
| Settings window | Renders; `artifacts/settings-window2.png` |
| Keyboard enumeration | Lists the HID keyboard; PS/2/soft keyboards filtered |
| Light/dark theme + native title bar | Both render correctly |
| WMS endpoint | Loopback pair created; public `Midi Broad` endpoint routes A↔B |
| **End-to-end Note On/Off** | **Passed** with the earlier virtual-device build; loopback service-level routing probe passed (`20903C64` received on the paired endpoint) |
| Pitch bend centre + All Notes Off | Received as `E0 00 40`, `B0 7B 00` after the note sequence |
| Language switching | English/Chinese packs load; Settings → Language switches live |
| Background hook | **Passed from a normal-integrity folder**: injected Z logged while Notepad was foreground |
| Chord display | **Passed**: Z+C+B shows `C` with `C 大调 · I 级 · C E G`; Z+C+B+N confirmed C6 detection |
| Tray hide-on-close | Removed — **X** exits the process by design |
| Single-instance activation | Named activation event restores and focuses the existing window, then exits |
| Reopen rendering | Verified after minimizing/restoring; hiding produced a black surface and is not used |
| Settings persistence | `config.json` beside the exe, per-user fallback, legacy `settings.json` migration |
| Config priority | Verified: `config.json` wins over a newer legacy file; legacy migrates when no config exists |
| Config export/import | JSON round-trip preserves mappings, port name, theme and behavior |
| Momentary Shift octave | Verified: hold Left Shift shows +1 octave; release restores the saved octave |
| Pedal indicators | Four coloured F1–F4 indicators render above the piano; off state is dimmed |
| Physical key hook (independent probe) | Z/X/C arrive as scan codes `2C/2D/2E` |
| Physical keys in MBD | **Passed after removing Raw Input registration**: hook log shows `vk=0x5A scan=0x2C` |

Actual capture after a clean reboot:

```
MIDI 90 30 6B   Note On  C3 velocity 107
MIDI 80 30 00   Note Off C3
MIDI 90 34 56   Note On  E3 velocity 86
MIDI 80 34 00
MIDI 90 37 5C   Note On  G3 velocity 92
MIDI 80 37 00
MIDI B0 7B 00   All notes off
MIDI E0 00 40   Pitch bend centre
```

The WMS-native probe received the same messages as UMP (`2090306B`, etc.).

## WMS lifecycle limitation

The app now creates a **service-owned transient loopback pair** and leaves it in place when the app exits. The next launch reuses it by its stable endpoint IDs. This avoids the old virtual-device teardown that could wedge `midisrv`.

If the service is genuinely unresponsive, startup still offers the UAC-assisted `Restart-Service midisrv -Force` retry. A service restart/reboot removes transient loopbacks; the next launch recreates them.

Important: run the published app from a normal-integrity folder. A workspace folder carrying `Mandatory Label\Low Mandatory Level` makes the process low-integrity, and Windows then suppresses `WH_KEYBOARD_LL` events from normal apps when Midi Broad is not focused.

## How to run the end-to-end test

1. Reboot if the MIDI service has previously been left stuck.
2. Start the capture listener:

```powershell
python -u .\tools\capture-midi.py
```

3. In a second terminal, run:

```powershell
dotnet .\src\MidiBroad.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\MidiBroad.dll --selftest
```

Expected: three note pairs plus All Notes Off and pitch-bend centre, as above.

4. For the full path, start the app normally, choose the keyboard, press **Start listening**, play `Z X C V B N M` and `Q W E R T Y U I`.
   - If keys do not highlight, switch the active IME to English/direct input first. A Chinese IME can consume keys before the global hook.
5. Press **Ctrl + Alt + Shift + F12**; capture must stop and all notes must be released.
6. Close the window with **X**; the process must exit. Reopen it and confirm the virtual endpoint returns (or the UAC service-repair prompt appears).

## Environment notes

- Test machine: Windows 11 build 26300, x64, .NET SDK 10.0.302.
- `F:\Temp` has restrictive ACLs on this machine. Single-file .NET extraction fails here; the portable folder build in `dist\MidiBroad` is the supported distribution.
- The app exits on **X**. There is deliberately no tray/background mode; the WMS endpoint is disposed on exit and rebuilt on the next launch, with automatic `midisrv` repair if needed.
- Raw Input keyboard registration was the root cause of missing physical keys. Registering the keyboard for Raw Input in the same process silently prevented `WH_KEYBOARD_LL` callbacks from being delivered. The registration is removed; device enumeration still uses `GetRawInputDeviceList`, and hot-plug uses `WM_DEVICECHANGE`.
- The hook now runs on a dedicated message-loop thread; the WPF dispatcher thread proved unreliable for `WH_KEYBOARD_LL` on this machine.
- Minimize/restore is supported. `Hide()` produced a black WPF composition surface and is not used.
