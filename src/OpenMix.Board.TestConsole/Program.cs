using OpenMix.Board;
using OpenMix.Board.TestConsole;
using OpenMix.Cards;

if (args.Contains("--selftest"))
    return SelfTest.Run();

if (args.Contains("--dumpcards"))
{
    string path = args.FirstOrDefault(a => a.EndsWith(".csv")) ?? "data/cards.csv";
    var db = CardDatabase.LoadCsv(path);
    Console.WriteLine($"loaded {db.Count} cards from {path}");
    var byType = db.Cards.GroupBy(c => c.Type).OrderBy(g => g.Key);
    foreach (var g in byType) Console.WriteLine($"  {g.Key,-7} {g.Count()}");
    Console.WriteLine("sample CTI resolves:");
    foreach (var cti in new ushort[] { 58231, 40759, 21211 })
    {
        var c = db.Resolve(new Cti(cti));
        Console.WriteLine($"  CTI {cti} -> {(c is null ? "(unknown)" : $"{c.Name} by {c.Artist} [{c.Type}] {c.Key} {c.Mode} {c.Tempo}bpm pwr{c.Power}")}");
    }
    return 0;
}

if (args.Contains("--scan"))
{
    // list every BLE advertiser for a while, flagging DropMix boards
    var seen = new Dictionary<ulong, string>();
    var w = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher
    { ScanningMode = Windows.Devices.Bluetooth.Advertisement.BluetoothLEScanningMode.Active };
    w.Received += (_, e) =>
    {
        string name = e.Advertisement.LocalName ?? "";
        bool svc = e.Advertisement.ServiceUuids.Contains(BoardUuids.Service);
        lock (seen)
        {
            if (seen.TryGetValue(e.BluetoothAddress, out var had) && (had.Length > 0 || name.Length == 0)) return;
            seen[e.BluetoothAddress] = name;
        }
        if (name.Length == 0 && !svc) return;
        bool board = svc || name.StartsWith(BoardUuids.NamePrefix, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"{(board ? "BOARD " : "      ")}{e.BluetoothAddress:X12}  rssi {e.RawSignalStrengthInDBm,4}  '{name}'{(svc ? "  [card service]" : "")}");
    };
    Console.WriteLine("scanning 15s...");
    w.Start(); await Task.Delay(15000); w.Stop();
    Console.WriteLine($"done ({seen.Count} advertisers).");
    return 0;
}

// DropMix board test harness.
//   dotnet run --project OpenMix.Board.TestConsole -- [path\to\cards.csv]
// Scans for the physical board over Bluetooth LE, connects, then prints card
// add/remove events, the EQ button and battery level, and runs a short LED demo.
// Requires a real, powered-on, paired DropMix board; without one it just times out.

CardDatabase? cards = null;
if (args.FirstOrDefault(a => a.EndsWith(".csv")) is string csv && File.Exists(csv))
{
    cards = CardDatabase.LoadCsv(csv);
    Console.WriteLine($"loaded {cards.Count} cards from {csv}");
}

string Describe(Cti cti)
{
    var card = cards?.Resolve(cti);
    return card != null ? $"{card.Name} [{card.Type}] ({card.Cid})" : cti.ToString();
}

await using var board = new DropMixBoard();
board.Log += m => Console.WriteLine($"  [board] {m}");
if (args.Contains("--trace")) board.Trace = m => Console.WriteLine($"  ~ {m}");
board.CardChanged += e =>
    Console.WriteLine($"  {(e.Added ? "＋ PLACED " : "－ REMOVED")} slot {(int)e.Slot}: {Describe(e.Cti)}  (stack {e.StackCount})");
board.SlotResynced += s =>
    Console.WriteLine($"  = slot {(int)s.Slot} resynced: [{string.Join(", ", s.Ctis)}]");
board.ButtonPressed += b => Console.WriteLine($"  * button: {b}");
board.BatteryLevelChanged += p => Console.WriteLine($"  battery: {p}%");

Console.WriteLine("scanning for a DropMix board (15s)...");
if (!await board.ConnectAsync(TimeSpan.FromSeconds(15)))
{
    Console.WriteLine("no board found. Pair the board in Windows Bluetooth settings and try again.");
    return 0;
}

Console.WriteLine($"battery: {await board.ReadBatteryPercentAsync()}%");

// brief LED demo: light each slot's region a different colour
var demo = new LedColor[10];
LedColor[] palette = { new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0), new(1,0,1) };
for (int i = 0; i < 10; i++) demo[i] = palette[(i / 2) % palette.Length];
try { await board.SetLedStepTimeAsync(); await board.SetLedsAsync(demo); } catch { /* lights optional */ }

Console.WriteLine("polling — place and remove cards. Ctrl+C to quit.");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await board.RunPollLoopAsync(TimeSpan.FromMilliseconds(150), cts.Token);
Console.WriteLine("stopped.");
return 0;
