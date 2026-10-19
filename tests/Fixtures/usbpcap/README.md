# Lian Li wire fixtures (`*.rec`)

These are **not live captures**. They are the output of the project's own builders, regenerated in M9 with
`URGB_UPDATE_FIXTURES=1`, and they lock that output byte for byte. Each fixture holds two one-frame **loop uploads**
(`UploadLoopAsync`: static red with effect index 43 200 000, then a red→blue gradient with 43 200 001, interval 20 ms) to
one chain at channel 8 / rx 1. Keep-alive traffic (USB 17, RF 20) is removed.

| Part of each frame | Source |
|---|---|
| 64-byte reports, USB opcode at byte 0 | IL-pinned (`slv3.WinUsbLed`, M5b) |
| Relay header `[16][chunk 0..3][channel][rx]` + 60 packet bytes | IL-pinned (`MasterDevice.SendRfData`, M5c) |
| 240-byte RF packet: `[18][RF_CMD][target MAC][master MAC]` | IL-pinned (`MasterDevice`, M5c) |
| RF 32 upload: effect index, packet index/count, header fields (interval as whole ms + hundredths, M9 fix), 220-byte data chunks, header sent ×4 | IL-pinned (`MasterDevice.SyncRgbData`, M5c) |
| LEDs per fan, R,G,B order | IL-pinned (`RfDevice.LedNum`, `Util.GetRgbData`); INFV3 stride 50 vs LedNum 44 unresolved |
| Animation bytes inside the data packets | **Identity stand-in, not tinyuz.** M9 loads the real codec (`yuz.dll` from the L-Connect 3 directory, never copied); the fixtures deliberately keep the stand-in so they do not depend on a vendor binary. `YuzCodecTests` exercise the real dll separately. |

The HS2, L88 and Strimer fixtures were removed in M5b: those are separate USB devices, not RF-relayed.

Live usbpcap captures of L-Connect (`tools/capture-usbpcap.ps1`) are the ARCHITECTURE-D2 §3 flip criteria ([HW], pending):
a StaticColor upload must match our bytes (real codec) except the effect index; a Rainbow upload must match the header fields.
