using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace OpenMix.Board;

/// <summary>Windows (WinRT GATT) driver for the physical DropMix board. Scans for the
/// board by its card-service UUID / name, connects, subscribes to notifications, and
/// exposes the reverse-engineered protocol: poll the slots, detect card add/remove,
/// drive the LEDs, read the battery and firmware, and relay the EQ button.
///
/// Not yet tested against hardware (no board available during development); the wire
/// protocol matches the RE notes and the pure layer in <see cref="BoardProtocol"/>.</summary>
public sealed class DropMixBoard : IAsyncDisposable
{
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _reqOp, _dataTx, _opSts, _btnSts, _imdLed, _ledBrtSeq, _ledTime, _battery, _firmware;

    private readonly SemaphoreSlim _reqLock = new(1, 1);
    private readonly BoardProtocol.FrameReassembler _reassembler = new();
    private readonly SlotTracker _tracker = new();
    private TaskCompletionSource<BoardProtocol.Frame>? _pending;

    public event Action<CardEvent>? CardChanged;

    /// <summary>While true, slot polling pauses (the app suppresses card-count requests during the wheel,
    /// coin flip and Clash start) and resumes reading afterwards.</summary>
    public Func<bool>? SuspendPolling { get; set; }

    /// <summary>What the tracker believes is on each game slot (bottom → top).</summary>
    public IReadOnlyList<Cti>[] Contents() => Enumerable.Range(0, 5).Select(i => _tracker.Contents((Slot)i)).ToArray();
    public int CardCount => Contents().Sum(c => c.Count);

    /// <summary>Seed the tracker with known stacks (after a reconnect).</summary>
    public void Seed(IReadOnlyList<Cti>[] stacks) { for (int i = 0; i < 5 && i < stacks.Length; i++) _tracker.Seed((Slot)i, stacks[i]); }

    /// <summary>BluetoothBoardReader kAutomatic read size: Tiny if &lt;10 cards and no stack ≥4; Small if a stack has
    /// 4–6 or there are 10–24 cards; Medium if &lt;45 cards and no stack ≥10; else Large.</summary>
    public static BoardProtocol.ReadSize AutoReadSize(int total, int tallest) =>
        total < 10 && tallest < 4 ? BoardProtocol.ReadSize.Tiny
        : tallest <= 6 && total < 25 ? BoardProtocol.ReadSize.Small
        : total < 45 && tallest < 10 ? BoardProtocol.ReadSize.Medium
        : BoardProtocol.ReadSize.Large;
    public event Action<SlotContents>? SlotResynced;
    public event Action<BoardButton>? ButtonPressed;
    public event Action<int>? BatteryLevelChanged;      // percent
    public event Action<string>? Log;
    /// <summary>Raw traffic trace (hex), for tuning against hardware. Null = off.</summary>
    public Action<string>? Trace { get; set; }
    /// <summary>Raised when the BLE link drops after a successful connect.</summary>
    public event Action? Disconnected;
    public string? DeviceName => _device?.Name;

    public string? FirmwareVersion { get; private set; }
    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    // ---- connection ------------------------------------------------------------

    /// <summary>Scan for a board and connect. Returns false on timeout.</summary>
    /// <summary>GetBluetoothRSSIConnectThreshold: adverts weaker than this (dBm) don't connect (-60); the connect
    /// panel's retry sets IgnoreRssi (the original's threshold -999).</summary>
    public int RssiThreshold { get; set; } = -60;
    public bool IgnoreRssi { get; set; }
    /// <summary>A board was heard but too far away (BluetoothConnectionChecker OutOfRange).</summary>
    public event Action<short>? OutOfRange;
    /// <summary>BluetoothBoardReader: not board-ready within 8 s of connecting → disconnect, connection failed.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(8);

    public async Task<bool> ConnectAsync(TimeSpan timeout, bool includeOta = false)
    {
        ulong address = await ScanForBoardAsync(timeout, includeOta).ConfigureAwait(false);
        if (address == 0) return false;
        var ready = ConnectToAsync(address);
        if (await Task.WhenAny(ready, Task.Delay(ReadyTimeout)).ConfigureAwait(false) != ready)
        {
            Log?.Invoke("BLE Connect: connection failed (not ready within 8 s)");
            ConnectFailed = true;
            try { _device?.Dispose(); } catch { }
            return false;
        }
        bool ok = await ready.ConfigureAwait(false);
        ConnectFailed = !ok;
        return ok;
    }
    /// <summary>The last attempt found a board but couldn't get it ready (the panel's "ERROR CONNECTING").</summary>
    public bool ConnectFailed { get; private set; }

    async Task<bool> ConnectToAsync(ulong address)
    {

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (_device == null) { Log?.Invoke("FromBluetoothAddressAsync returned null"); return false; }
        _device.ConnectionStatusChanged += (d, _) =>
        {
            Log?.Invoke($"link {d.ConnectionStatus}");
            if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected && _ready) { _ready = false; Disconnected?.Invoke(); }
        };
        await EnsurePairedAsync(_device).ConfigureAwait(false);
        // The app calls requestConnectionPriority(HIGH) after connecting; the Windows 11 equivalent is the
        // throughput-optimised connection interval, which keeps LED/poll writes from queueing.
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsMethodPresent("Windows.Devices.Bluetooth.BluetoothLEDevice", "RequestPreferredConnectionParameters"))
            {
                var r = _device.RequestPreferredConnectionParameters(BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
                Log?.Invoke($"connection priority: {r.Status}");
            }
        }
        catch (Exception ex) { Log?.Invoke($"connection priority: {ex.Message}"); }

        var svcResult = await _device.GetGattServicesForUuidAsync(BoardUuids.Service, BluetoothCacheMode.Uncached);
        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
        {
            Log?.Invoke($"card service not found ({svcResult.Status})");
            return false;
        }
        GattDeviceService svc = svcResult.Services[0];

        _reqOp     = await GetChar(svc, BoardUuids.ReqOp);
        _dataTx    = await GetChar(svc, BoardUuids.DataTx);
        _opSts     = await GetChar(svc, BoardUuids.OpSts);
        _btnSts    = await GetChar(svc, BoardUuids.BtnSts);
        _imdLed    = await GetChar(svc, BoardUuids.ImdLed);
        _ledBrtSeq = await GetChar(svc, BoardUuids.LedBrtSeq);
        _ledTime   = await GetChar(svc, BoardUuids.LedTime);

        if (_reqOp == null || _dataTx == null || _opSts == null)
        {
            Log?.Invoke("required characteristics missing");
            return false;
        }

        _dataTx.ValueChanged += OnDataTx;
        await Subscribe(_dataTx);
        await Subscribe(_opSts!);
        if (_btnSts != null) { _btnSts.ValueChanged += OnButton; await Subscribe(_btnSts); }

        foreach (var (n, c) in new[] { ("REQ_OP", _reqOp), ("DATA_TX", _dataTx), ("OP_STS", _opSts), ("BTN_STS", _btnSts), ("IMD_LED", _imdLed), ("LED_BRT_SEQ", _ledBrtSeq), ("LED_TIME", _ledTime) })
            Trace?.Invoke($"{n}: {(c == null ? "missing" : c.CharacteristicProperties.ToString())}");
        _opSts.ValueChanged += (_, a) => Trace?.Invoke($"rx OP_STS {Hex(ToBytes(a.CharacteristicValue))}");

        await LoadDeviceInfoAsync();
        _ready = true;
        Log?.Invoke($"connected to {_device.Name} (fw {FirmwareVersion ?? "?"})");
        return true;
    }

    private bool _ready;

    /// <summary>The board only allows bonded links. Pair from code (Just Works / confirm) so a
    /// console or headless host needs no trip to Windows Settings; already-paired boards skip this.</summary>
    private async Task EnsurePairedAsync(BluetoothLEDevice dev)
    {
        var pairing = dev.DeviceInformation.Pairing;
        if (pairing.IsPaired) { Log?.Invoke("already paired"); return; }
        if (!pairing.CanPair) { Log?.Invoke("board reports it cannot pair; trying unpaired"); return; }
        Log?.Invoke("pairing...");
        var custom = pairing.Custom;
        void OnReq(DeviceInformationCustomPairing s, DevicePairingRequestedEventArgs e) => e.Accept();
        custom.PairingRequested += OnReq;
        try
        {
            var r = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.None, DevicePairingProtectionLevel.Encryption);
            if (r.Status != DevicePairingResultStatus.Paired && r.Status != DevicePairingResultStatus.AlreadyPaired)
                r = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.None, DevicePairingProtectionLevel.None);
            Log?.Invoke($"pairing: {r.Status}");
        }
        finally { custom.PairingRequested -= OnReq; }
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    private async Task<ulong> ScanForBoardAsync(TimeSpan timeout, bool includeOta)
    {
        var tcs = new TaskCompletionSource<ulong>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        void OnReceived(BluetoothLEAdvertisementWatcher w, BluetoothLEAdvertisementReceivedEventArgs e)
        {
            bool hasService = e.Advertisement.ServiceUuids.Contains(BoardUuids.Service);
            string name = e.Advertisement.LocalName ?? "";
            bool nameOk = name.StartsWith(BoardUuids.NamePrefix, StringComparison.OrdinalIgnoreCase)
                          || (includeOta && name == BoardUuids.OtaName);
            if (hasService || nameOk)
            {
                if (!IgnoreRssi && e.RawSignalStrengthInDBm < RssiThreshold) { OutOfRange?.Invoke(e.RawSignalStrengthInDBm); return; }
                Log?.Invoke($"found '{name}' ({e.BluetoothAddress:X12}, rssi {e.RawSignalStrengthInDBm})");
                tcs.TrySetResult(e.BluetoothAddress);
            }
        }
        watcher.Received += OnReceived;
        watcher.Start();
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
            return done == tcs.Task ? tcs.Task.Result : 0UL;
        }
        finally { watcher.Received -= OnReceived; watcher.Stop(); }
    }

    private static async Task<GattCharacteristic?> GetChar(GattDeviceService svc, Guid uuid)
    {
        var r = await svc.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        return r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0
            ? r.Characteristics[0] : null;
    }

    private async Task Subscribe(GattCharacteristic c)
    {
        var kind = c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        var st = await c.WriteClientCharacteristicConfigurationDescriptorAsync(kind);
        if (st != GattCommunicationStatus.Success) Log?.Invoke($"subscribe {c.Uuid}: {st}");
    }

    private async Task LoadDeviceInfoAsync()
    {
        if (_device == null) return;
        var dis = await _device.GetGattServicesForUuidAsync(BoardUuids.DeviceInfoService);
        if (dis.Status == GattCommunicationStatus.Success && dis.Services.Count > 0)
        {
            _firmware = await GetChar(dis.Services[0], BoardUuids.FirmwareRevision);
            if (_firmware != null)
            {
                var rd = await _firmware.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (rd.Status == GattCommunicationStatus.Success)
                    FirmwareVersion = DataReader.FromBuffer(rd.Value).ReadString(rd.Value.Length);
            }
        }
        var bat = await _device.GetGattServicesForUuidAsync(BoardUuids.BatteryService);
        if (bat.Status == GattCommunicationStatus.Success && bat.Services.Count > 0)
            _battery = await GetChar(bat.Services[0], BoardUuids.BatteryLevel);
    }

    // ---- request / response ----------------------------------------------------

    private void OnDataTx(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[] raw = ToBytes(args.CharacteristicValue);
        Trace?.Invoke($"rx DATA_TX {Hex(raw)}");
        BoardProtocol.Frame? frame = _reassembler.Feed(raw);
        if (_reassembler.NeedsContinue)
        {
            // pull the next fragment
            WriteAsync(_opSts!, BoardProtocol.ContinueCommand.ToArray()).ContinueWith(t => Log?.Invoke($"continue failed: {t.Exception?.InnerException?.Message}"), TaskContinuationOptions.OnlyOnFaulted);
            return;
        }
        if (frame is { } f) _pending?.TrySetResult(f);
    }

    private void OnButton(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[] b = ToBytes(args.CharacteristicValue);
        Trace?.Invoke($"rx BTN_STS {Hex(b)}");
        if (b.Length > 0 && Enum.IsDefined(typeof(BoardButton), (int)b[0]))
            ButtonPressed?.Invoke((BoardButton)b[0]);
    }

    /// <summary>Send a request on REQ_OP and await the matching reassembled response.</summary>
    private async Task<BoardProtocol.Frame> RequestAsync(byte[] request, TimeSpan? timeout = null)
    {
        await _reqLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _pending = new TaskCompletionSource<BoardProtocol.Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WriteAsync(_reqOp!, request).ConfigureAwait(false);
            var done = await Task.WhenAny(_pending.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(2)))
                                 .ConfigureAwait(false);
            if (done != _pending.Task) throw new TimeoutException($"no response to op 0x{request[0]:X2}");
            return _pending.Task.Result;
        }
        finally { _pending = null; _reqLock.Release(); }
    }

    // ---- high-level operations -------------------------------------------------

    /// <summary>Poll all slots once; emit card add/remove events, resolving ambiguous
    /// changes with an A5 full read.</summary>
    public async Task PollAsync()
    {
        var (total, tallest) = _tracker.Heights();
        var poll = await RequestAsync(BoardProtocol.BuildPoll(0x1F, AutoReadSize(total, tallest)));
        if (poll.Opcode != BoardProtocol.OpPoll) return;
        var summaries = BoardProtocol.ParsePoll(poll.Payload);

        var events = _tracker.Update(summaries, out var needFull);
        foreach (var ev in events) CardChanged?.Invoke(ev);

        foreach (var slot in needFull)
        {
            var read = await RequestAsync(BoardProtocol.BuildReadCtis(BoardProtocol.SlotMaskBit(slot)));
            if (read.Opcode != BoardProtocol.OpReadCtis) continue;
            var ctis = BoardProtocol.ParseCtis(read.Payload);
            foreach (var ev in _tracker.ApplyFullRead(slot, ctis)) CardChanged?.Invoke(ev);
            SlotResynced?.Invoke(new SlotContents(slot, ctis));
        }
    }

    /// <summary>Continuously poll until the token is cancelled.</summary>
    public async Task RunPollLoopAsync(TimeSpan interval, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { if (SuspendPolling?.Invoke() != true) await PollAsync(); }
            catch (Exception ex) { Log?.Invoke($"poll error: {ex.Message}"); }
            try { await Task.Delay(interval, token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Read the 64-bit UIDs of the cards in a slot (A4).</summary>
    public async Task<ulong[]> ReadUidsAsync(Slot slot)
    {
        var f = await RequestAsync(BoardProtocol.BuildReadUids(BoardProtocol.SlotMaskBit(slot)));
        return f.Opcode == BoardProtocol.OpReadUids ? BoardProtocol.ParseUids(f.Payload) : Array.Empty<ulong>();
    }

    /// <summary>Request the ECDSA signature bytes for a card UID (A6). A 3-byte payload
    /// means the board reported failure (returns empty).</summary>
    public async Task<byte[]> ReadSignatureAsync(ulong uid, TimeSpan? timeout = null)
    {
        var f = await RequestAsync(BoardProtocol.BuildSignatureRequest(uid), timeout ?? TimeSpan.FromSeconds(4));
        return f.Payload.Length <= 3 ? Array.Empty<byte>() : f.Payload;
    }

    public Task SetLedsAsync(IReadOnlyList<LedColor> leds10) =>
        WriteAsync(_imdLed!, BoardProtocol.EncodeImmediateLeds(leds10));

    /// <summary>IMD_LED with 20 pre-encoded bytes (see <see cref="BoardLeds"/>).</summary>
    public Task SetLedsRawAsync(byte[] bytes20) => _imdLed != null ? WriteAsync(_imdLed, bytes20) : Task.CompletedTask;

    /// <summary>LED_BRT_SEQ with 20 pre-encoded bytes (5 slots × 8 nibbles).</summary>
    public Task SetBrightnessRawAsync(byte[] bytes20) => _ledBrtSeq != null ? WriteAsync(_ledBrtSeq, bytes20) : Task.CompletedTask;

    public Task SetBrightnessSequenceAsync(float[][] perSlot8) =>
        WriteAsync(_ledBrtSeq!, BoardProtocol.EncodeBrightnessSeq(perSlot8));

    public Task SetLedStepTimeAsync(byte value = 0x14) =>
        _ledTime != null ? WriteAsync(_ledTime, BoardProtocol.LedTime(value)) : Task.CompletedTask;

    public async Task<int> ReadBatteryPercentAsync()
    {
        if (_battery == null) return -1;
        var rd = await _battery.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (rd.Status != GattCommunicationStatus.Success || rd.Value.Length == 0) return -1;
        int pct = ToBytes(rd.Value)[0];
        BatteryLevelChanged?.Invoke(pct);
        return pct;
    }

    // ---- buffer helpers --------------------------------------------------------

    private async Task WriteAsync(GattCharacteristic c, byte[] data)
    {
        IBuffer buf = CryptographicBuffer.CreateFromByteArray(data);
        var opt = c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        Trace?.Invoke($"tx {c.Uuid.ToString()[..8]} {Hex(data)}");
        var r = await c.WriteValueWithResultAsync(buf, opt);
        if (r.Status != GattCommunicationStatus.Success)
            throw new IOException($"write {c.Uuid.ToString()[..8]}: {r.Status}{(r.ProtocolError is byte e ? $" (0x{e:X2})" : "")}");
    }

    private static byte[] ToBytes(IBuffer buffer)
    {
        CryptographicBuffer.CopyToByteArray(buffer, out byte[] bytes);
        return bytes ?? Array.Empty<byte>();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_dataTx != null) _dataTx.ValueChanged -= OnDataTx;
            if (_btnSts != null) _btnSts.ValueChanged -= OnButton;
            _device?.Dispose();
        }
        catch { /* ignore */ }
        await Task.CompletedTask;
    }
}
