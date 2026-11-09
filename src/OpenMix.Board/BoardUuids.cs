namespace OpenMix.Board;

/// <summary>GATT UUIDs used by the DropMix board, from the reverse-engineered
/// <c>BluetoothBoardReader</c>. The card service and its characteristics share the
/// suffix <c>-2730-4BB8-B160-502596E4C2FE</c>; the 16-bit prefix selects the role.</summary>
public static class BoardUuids
{
    /// <summary>Primary card service; also the advertisement scan filter.</summary>
    public static readonly Guid Service   = Guid.Parse("0001cfd4-2730-4bb8-b160-502596e4c2fe");
    public static readonly Guid ReqOp     = Guid.Parse("0002cfd4-2730-4bb8-b160-502596e4c2fe"); // write requests
    public static readonly Guid DataTx    = Guid.Parse("0003cfd4-2730-4bb8-b160-502596e4c2fe"); // notify: responses
    public static readonly Guid OpSts     = Guid.Parse("0004cfd4-2730-4bb8-b160-502596e4c2fe"); // notify; write 60 00 = continue
    public static readonly Guid BtnSts    = Guid.Parse("0005cfd4-2730-4bb8-b160-502596e4c2fe"); // notify: buttons
    public static readonly Guid ImdLed    = Guid.Parse("0006cfd4-2730-4bb8-b160-502596e4c2fe"); // write: immediate LEDs
    public static readonly Guid LedBrtSeq = Guid.Parse("0007cfd4-2730-4bb8-b160-502596e4c2fe"); // write: brightness sequence
    public static readonly Guid LedTime   = Guid.Parse("0008cfd4-2730-4bb8-b160-502596e4c2fe"); // write: step timing (0x14)

    // Standard GATT
    public static readonly Guid BatteryService    = Guid.Parse("0000180f-0000-1000-8000-00805f9b34fb");
    public static readonly Guid BatteryLevel       = Guid.Parse("00002a19-0000-1000-8000-00805f9b34fb");
    public static readonly Guid DeviceInfoService  = Guid.Parse("0000180a-0000-1000-8000-00805f9b34fb");
    public static readonly Guid FirmwareRevision   = Guid.Parse("00002a26-0000-1000-8000-00805f9b34fb");

    /// <summary>Advertised-name prefix of a board in normal mode.</summary>
    public const string NamePrefix = "DropMix";
    /// <summary>Exact advertised name when the board is in OTA bootloader mode.</summary>
    public const string OtaName = "Dropmix OTA";
}
