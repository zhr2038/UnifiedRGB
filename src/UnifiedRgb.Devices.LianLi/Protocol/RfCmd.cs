namespace UnifiedRgb.Devices.LianLi.Protocol;

/// <summary>
/// Wireless opcodes (slv3.RF_CMD, analysis 02 §4): byte 1 of the 240-byte RF packet (<c>MasterDevice</c>, analysis/il).
/// Lists ONLY the lighting/status opcodes allowed by ARCHITECTURE §8 (+ RF 21 under the ARCHITECTURE-D2 §5 gate). Not
/// implemented and therefore absent: 19, 22, 34, 35. Firmware opcodes deliberately have no member here.
/// </summary>
public enum RfCmd : byte
{
    /// <summary>
    /// Bind/unbind AND fan PWM (<c>SyncControlInfo</c>/<c>SyncPwm</c>: [16] slave index, [17..20] PWM per fan). Not sent in
    /// v1: a bind needs the device's current PWM, which needs the receiver device list (SELF_AUDIT M5c).
    /// </summary>
    Bind = 16,

    /// <summary>"Selected" in the L-Connect UI (identify the device); not sent by this adapter.</summary>
    Select = 18,
    ClockSync = 20,

    /// <summary>
    /// Persist the current lighting in the devices' flash (<c>MasterDevice.SaveConfig</c>). Never sent automatically: only
    /// <see cref="LianLiAdapter.SaveConfigAsync"/> (explicit GUI action, after a completed upload, ≤ 1 per 60 s), D2 §5.
    /// </summary>
    SaveConfig = 21,
    SwitchWirelessTheme = 25,

    /// <summary>Animation upload (<c>SyncRgbData</c>): header packet, then 220-byte chunks of the compressed animation.</summary>
    RgbSync = 32,
    V150MBSyncSwitch = 36,
    V150LightSyncSwitch = 38,
    TLv3LedLightSyncSwitch = 39,
}
