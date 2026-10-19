namespace UnifiedRgb.Devices.LianLi.Protocol;

/// <summary>
/// USB opcodes this application may send to the RF sender dongle (slv3.USB_CMD, analysis 02 §4), narrowed in M5c to what
/// <c>MasterDevice</c> actually sends (analysis/il): 16 = relay one 60-byte chunk of an RF packet (<c>SendRfData</c>),
/// 17 = query the dongle's MAC, clock and version (<c>QuerryMasterMac</c>). The IL never sends 14 (SendRfLong) or 18
/// (SendHeart), so neither is here. Firmware opcodes deliberately have no member here.
/// </summary>
public enum UsbCmd : byte
{
    SendRf = 16,
    GetMac = 17,
}
