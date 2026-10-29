# ROG receiver lighting foundation

Native Windows HID implementation for Azoth X and Harpe II Extreme Edition20 receivers. It uses the existing transport abstraction; no Armoury Crate, GearLink, OpenRGB process, downloaded runtime DLL or copied third-party implementation is required.

## Current publication boundary (2026-10-29)

This source includes the Host/App peripheral integration, native input/menu support, keyboard OLED preparation and display services, and explicit per-key admission and local-map support. Hardware output and native presentation remain gated by local configuration and current device capabilities; source defaults are off and no live receiver pins or maps are shipped.

The locally installed custom native firmware was accepted on October28 for typing, animation and native controls. Firmware binaries and device-specific deployment state are not distributed here. The separate October29 Logo bitmap candidate had not been flashed at this publication checkpoint. After the software per-key initialization correction, the owner confirmed one red Esc key on both USB and RF; the complete81-key map, full spatial effects and shared optical synchronization remain unaccepted. The dated sections below retain earlier evidence and limitations as history, not the current implementation inventory.

## Implemented

- Access-zero passive HID metadata discovery for `0B05:1ACE/1AD0`, MI02, vendor pages FF02/FF00/FF01, 64-byte input/output collections.
- Physical-receiver grouping with opaque route pins. Paired product IDs, control report IDs and notification report IDs come from A0 00. Notification ids4/5 are not device-type codes.
- One serialized, bounded transaction owner across each receiver's channels. Complete keyboard frames do not interleave with queries. Failure invalidates the connection generation; no automatic command retry.
- Strict known queries and direct Azoth X `C0 81` framing. At most fourteen distinct LED records per report; every record's blue byte is retained.
- Keyboard whole-colour `51 2C` and mouse area0/static `51 28` are write-only paths. Readback is a separate query, never an invented ACK wait. Bounded query demultiplexing discards old lighting/status reports while still requiring the exact response header and a total deadline; no automatic resend.
- Complete keyboard effect snapshots use the exact keyboard enum, independent of the mouse enum: Rainbow4/Ripple5 have three descending chunks, Quicksand7 two, and supported simple modes one. Temporary tests preserve the inactive static bank too.
- Explicit owner sleep-policy operations are separate from RGB. They require a current power snapshot and verify the requested idle value and unchanged other power settings; scene frames cannot invoke them.
- Integration through existing Host adapter/factory, native bring-up lease, writer guard, frame pacing and shared scene rendering. No independent render clock or hardware Host.

## Foundation gates and limits (historical, 2026-10-23)

Source configuration keeps `EnableAsusRog=false`, `Keyboards=[]`, `Mice=[]`. Enabling the adapter alone exposes metadata-only discovery ports; discovery and opening a bring-up lease send nothing. Explicit probes send only their named queries. A normal scene writer additionally requires local route pins, `ControlVerified`, and current identity/readback validation.

No automatic LED map, general animated mouse mode, OLED output/input event reader, profile/save, pairing change, DPI/polling, reset or firmware operation is exposed. `ControlVerified` and `KeyOrderVerified` are local settings; a device reply never sets them. Whole-colour acceptance does not establish per-key mapping or dynamic optical synchronization.

The keyboard `WholeColorOnly` path needs no invented LED indices and preserves the observed hardware brightness; the direct-per-key path needs explicit local LED IDs and a verified ordering for spatial claims. Mouse area0 is one aggregate point. The default 10fps is a conservative **host** ceiling, not measured RGB refresh. Command echoes do not establish atomic display or shared presentation timestamps. Normal adapter close sends nothing; bounded test helpers explicitly restore snapshots.

Local configuration uses `AsusRog.Keyboards[]` with `ReceiverKey` (64 hex characters), `ReportId`, `WholeColorOnly`, `LedIds` (ConfigurationBinder byte arrays use base64), `ControlVerified` and `KeyOrderVerified`; `Mice[]` has receiver/route pins and a separate control gate. Whole-colour bindings use no LED map. No live pins/maps are shipped. These are local device settings, not scene-package metadata.

## Real-device evidence, 2026-10-23

The owner confirms both use2.4GHz. A0 00 confirms Azoth X RF `1C25/control2/notify4` on1ACE and P725 RF `1D7C/control3/notify5` on1AD0. Initial FFAA replies disappeared after the owner's physical wake, with identical query bytes. Both originally slept after1minute; the owner explicitly requested disabling sleep. Idle255 was written/read back for both, with other power fields unchanged. It survived handle close/reopen; power-cycle persistence remains untested, and no profile-save command was sent.

Mouse six-second white/restore tests matched readback, and the owner accepted actual white then original orange. Keyboard six-second white/restore was also optically accepted. The first keyboard restoration reported failure because four old512C echoes preceded its query; an explicit recovery verified the original Ripple5 three-packet palette, static bank and profile6. The bounded query drain was corrected, and the subsequent fully automatic test restored both banks without error. These are limited temporary-colour/restore acceptances, not per-key, long-running scene-sync or deployment acceptance.

Failed logs and command evidence are retained privately. Earlier firmware/Tier0 reports are historical research, not implementation authority; their claims require independent validation. No firmware/OLED/HA implementation was enabled by this initial RGB foundation.

The independent review traced the published M7041.00.25 sample's image/banner61/62 upload through RAM staging to external serial-memory erase/program operations; omitting a profile save does not make it a safe live-frame API. A separate67 RAM path remains an unverified research lead. SUIT container/digest structure alone also does not establish firmware signing enforcement or recovery. These sample-specific findings neither change the RGB protocol nor authorize new device operations.

Later isolated hardware validation confirmed keyboard1.00.24/receiver7.00.08 and corrected the Windows event-collection assumption: the paired keyboard notification RID4 is on MI03/FFC0, joined to MI02 through fresh PnP container identity; FFC2 advertises RID3. A15-second shared read-only idle observation completed without actions or admitted control events. Physical72 mapping/release/repeats were pending owner actuation. A single24/01 mode getter did not produce a matching reply; no pixels or settings were written. Installed M704 native code supplies67 logical packet builders, but its selected RF wrapper, replies and exact restoration were unresolved at that checkpoint. No production event reader, OLED or HA feature was enabled by those findings alone.

The App now has a shared case-power workflow and a 208×64 Chinese control-display **software preview** in the case overview.
OFF preserves the recovery point on repeated requests; the explicit ON intent preserves an already running scene. Screen
content and local device settings are not included in those writes. The preview distinguishes requests from Host screen
feedback, including pending, missing, disconnected and expired observations. It is not sent to the keyboard. Physical lever
mapping, OS side effects and the keyboard OLED transport remain unverified and disabled; the owner deferred physical tests.
The client-only update was verified with614 App/Host tests and130 tests against installed client dependencies, not a new full
solution or optical acceptance. Detailed local evidence is retained privately.

The later owner-correlated RF test confirmed native21-byte/RID4 notifications: `72/01` Up, `72/02` middle press,
`72/04` Down and `72/00` neutral. Twenty such reports were captured while the owner confirmed the corresponding native
volume/mute functions and OLED widgets. That is positive basic event evidence, while hold/repeat timing and a control mode
without native volume side effects remain unverified. `RogLeverDecoder` is a pure, fail-closed edge decoder; it opens no
device and executes no action. No production listener or external OLED output has been enabled. Related tests80/80 and the
isolated complete Devices suite489/489 passed. The initial complete run's duplicate-codec fixture failure is retained.

## Current input and OLED validation (2026-10-23 evening)

`RogKeyboardInputObserver` is an explicit, read-only RF monitor. It joins control and event interfaces by fresh Windows
physical ContainerId, validates the opened input-only descriptor, drops ordinary report bodies, and publishes only typed
72/91 observations through a bounded queue. `RogReceiverSession` exposes the two owner-verified lever getters. There is
no automatic registration, case-action dispatch or default enable. Long holds do not invent repeated presses or releases.
The exact twelve-file selective merge passed33 new cases and522/522 complete Devices tests, with a separate clean main
build/replay; earlier failed new tests remain recorded. A real15-second idle open/cancel test succeeded without reports.

Direct USB is independently measured as1C24/MI01/FF00, native65 bytes and report ID0; RF64/RID2 must not be reused.
The stored animation getter returns3, while24/01 still refuses on both routes. Current custom-slot bindings are populated
(488/211/489); an input-action mode must preserve these, not assume it is empty. No remap or production listener is active.

Task-local wired OLED trials are separate from this product backend. Two finite67 frame uploads received correlated
ACKs, and same-preset61/0 plus stored-selector readback succeeded, but the owner saw only native status widgets: actual
custom presentation FAILED. Read-only settings then reported content enabled and content brightness0; this is a candidate
explanation, not yet an accepted fix. Do not confuse upload completion, a software preview or a selector ACK with optical
display/recovery acceptance. No OLED writer has been installed into Host. The local continuation records the current
research and test state, including preserved failure receipts and exact case-configuration protection checks.

## Later acceptance and current shipping boundary (2026-10-24)

The sustained **wired** OLED trial subsequently succeeded: the owner saw the text in Volume, Media, Brightness and Custom
modes. A separate prototype now reads the existing trusted Host and displays its real case status; the owner confirmed
restore followed by blackout, including the keyboard status remaining visible while all five case screens sleep.
Independent adjacent-pixel packing and larger, heavier text corrected the initially blurred small text and were optically
accepted. These are task-local trials, not an installed production OLED or lever-action feature. Wireless OLED remains
unverified. The test restored the original keyboard content brightness0 and stored selector3, and preserved the latest
owner-selected case state. Earlier failed presentation tests remain valid records.

The RGB adapter now preserves the mouse brightness read during each successful open, rather than forcing100 for every
scene frame; reopen refreshes that snapshot. Keyboard whole-colour output continues to preserve its observed brightness.
Five new fake cases cover mouse brightness and reopen; the before-fix run reproduced four failures, and the corrected
complete Devices suite passed527/527 in an isolated exact-binary review. These changes have not enabled source defaults,
installed receiver pins, established per-key mapping or certified optical animation synchronization.

The requested native **Guangyu** menu is a separate requirement: retain existing Volume/Media/Brightness/Custom entries,
add a distinct entry/page and preserve original Custom bindings. An overlay visible in every mode does not satisfy that
requirement. This was an outstanding requirement at the October24 checkpoint; see the October29 publication boundary above for the later local acceptance and distribution limits.

## Protocol provenance

Packet facts were independently implemented from published source evidence, not copied implementation code:

- [RIGStats receiver pairing](https://github.com/dvalfrid/rigstats/blob/4c277dcc8fb2e6eb876137171a510156907f7bf6/sensor-sidecar/Control/Lighting/OmniMouse.cs), pinned `4c277dcc8fb2e6eb876137171a510156907f7bf6`.
- [G-Helper keyboard framing](https://github.com/seerge/g-helper/blob/c09017453d9ed0c52261b67a48d02bd55232e6ac/app/Peripherals/Keyboard/AsusKeyboard.cs), pinned `c09017453d9ed0c52261b67a48d02bd55232e6ac`.
- [Official exact-model GearLink manifest](https://gearlink.asus.com/view/7204/manifest.json) and [P725 manifest](https://gearlink.asus.com/view/7548/manifest.json), saved hashes and offsets in the local research evidence.

The public sources are research references, not dependencies or a claim of permission to redistribute vendor assets. HID tests use synthetic paths and in-memory channels only.
