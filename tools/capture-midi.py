import ctypes
import time
from ctypes import wintypes

winmm = ctypes.WinDLL("winmm")


class MIDIINCAPSW(ctypes.Structure):
    _fields_ = [
        ("wMid", wintypes.WORD),
        ("wPid", wintypes.WORD),
        ("vDriverVersion", wintypes.UINT),
        ("szPname", wintypes.WCHAR * 32),
        ("dwSupport", wintypes.DWORD),
    ]


def find_port(name="Midi Broad"):
    for i in range(winmm.midiInGetNumDevs()):
        caps = MIDIINCAPSW()
        winmm.midiInGetDevCapsW(i, ctypes.byref(caps), ctypes.sizeof(caps))
        if caps.szPname == name:
            return i
    return None


port = None
for _ in range(80):
    port = find_port()
    if port is not None:
        break
    time.sleep(0.25)

if port is None:
    raise SystemExit("Midi Broad input port not found")

print(f"Using MIDI input port {port}: Midi Broad", flush=True)
messages = []
CALLBACK_FUNCTION = 0x00030000
MIM_DATA = 0x3C3
CB = ctypes.WINFUNCTYPE(
    None,
    wintypes.HANDLE,
    wintypes.UINT,
    ctypes.c_size_t,
    ctypes.c_size_t,
    ctypes.c_size_t,
)


@CB
def callback(handle, message, instance, param1, param2):
    if message == MIM_DATA:
        messages.append(param1)
        print(
            f"MIDI {param1 & 0xFF:02X} {(param1 >> 8) & 0xFF:02X} {(param1 >> 16) & 0xFF:02X}",
            flush=True,
        )


handle = wintypes.HANDLE()
winmm.midiInOpen.argtypes = [
    ctypes.POINTER(wintypes.HANDLE),
    wintypes.UINT,
    ctypes.c_size_t,
    ctypes.c_size_t,
    wintypes.DWORD,
]
winmm.midiInOpen.restype = wintypes.UINT
result = winmm.midiInOpen(
    ctypes.byref(handle), port, ctypes.cast(callback, ctypes.c_void_p).value, 0, CALLBACK_FUNCTION
)
print(f"midiInOpen result={result}", flush=True)
winmm.midiInStart(handle)

try:
    for _ in range(120):
        time.sleep(0.25)
finally:
    winmm.midiInStop(handle)
    winmm.midiInReset(handle)
    winmm.midiInClose(handle)

print(f"Captured {len(messages)} short messages.", flush=True)
