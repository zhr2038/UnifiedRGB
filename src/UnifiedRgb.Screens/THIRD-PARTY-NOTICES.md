# Third-party notices — UnifiedRgb.Screens

## turing-smart-screen-cli (TURZX 1CBE:0088 image push)

The TURZX framing in `Protocol/ScreenProtocol.cs` and the TURZX family facts in `ScreenFamilies.cs` (VID 1CBE /
PID 0088, native 480x1920 raster, PNG push command 102, the 500-byte plain header DES-CBC encrypted with zero padding
to 504 bytes inside a 512-byte block ending A1 1A, the big-endian payload length and the 524288-byte single-PNG
bound) were derived from:

- Repository: https://github.com/phstudy/turing-smart-screen-cli
- Commit: ffc652ab7fa421ff57e7a2bf67c20bfccb884784 (`src/turingscreencli/operations.py` `_send_png_bytes` and `send_brightness_command`,
  `src/turingscreencli/transport.py`)
- Captured copy: `analysis/evidence-extract/screens-native-20261018/turzx-*` and `reference.json`

This is an independent C# reimplementation of image push and the narrowly typed volatile backlight command14
(level at byte8), used to turn the case display's backlight off during whole-case blackout and restore a configured
wake level. No source file of that project is included. Storage, video, persistent-device settings and reset operations
are not implemented. The image-push API still rejects backlight/control commands; the separate backlight builder can
only construct command14 and never writes device flash.

```
MIT License

Copyright (c) 2025 Study Hsueh

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
