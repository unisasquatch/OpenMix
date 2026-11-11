// OpenMix host: runs the shared table, plays the mix, optionally drives a DropMix board, and serves
// the phone web app on the local network. Game content is imported from the user's own copy of the game
// (see OpenMix.Importer) into %LOCALAPPDATA%\OpenMix\content, or passed with --content.
//
//   dotnet run --project src/OpenMix.Host -- [--port 5080] [--board] [--no-audio] [--hand 5]
//                                            [--grace 15] [--content <dir>]
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using OpenMix.Audio;
using OpenMix.Board;
using OpenMix.Cards;
using OpenMix.Game;

if (args.Length > 0 && args[0] == "--import") { Environment.ExitCode = ImportCli.Run(args[1..]); return; }
var opt = Options.Parse(args);
string? foundRoot = opt.Root ?? TryFindContentRoot();
if (foundRoot == null)
{   // first run: no imported content yet -> the setup screen (import from the user's own APK + OBB), then restart
    await SetupMode.RunAsync(opt.Port, opt.NoWindow, args, (url, life) => RunTableWindow(url, life));
    return;
}
string root = foundRoot;
if (!opt.NoWindow)
{
    // app mode has no console window: keep the log in data/logs (overwritten each launch)
    var logDir = Path.Combine(root, "data", "logs"); Directory.CreateDirectory(logDir);
    var log = new StreamWriter(Path.Combine(logDir, "openmix.log"), append: false) { AutoFlush = true };
    Console.SetOut(log); Console.SetError(log);
}
try { var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, opt.Port); probe.Start(); probe.Stop(); }
catch (System.Net.Sockets.SocketException)
{
    Console.WriteLine($"OpenMix is already running on port {opt.Port}; not starting a second copy.");
    return;
}
string cardsCsv = Path.Combine(root, "data", "cards.csv");
// Card images imported for the cards the user owns (OpenMix.Importer.CardAssets)
string thumbDir = Path.Combine(root, "cardassets", "card_thumb");   // 384px JPEG (phones)
string pngDir = Path.Combine(root, "cardassets", "card_png");       // full 1024px PNG
string audioDir = Path.Combine(root, "audio_unpacked");

var db = CardDatabase.LoadCsv(cardsCsv);
var table = new Table(db.Cards) { OrphanGrace = TimeSpan.FromSeconds(opt.GraceSeconds) };

// Cards the user switched off in the collection (never shuffled into the virtual deck; still playable from the board).
string disabledPath = Path.Combine(root, "data", "disabled.json");
void SaveDisabled() { try { File.WriteAllText(disabledPath, System.Text.Json.JsonSerializer.Serialize(table.DisabledCids)); } catch (Exception e) { Console.WriteLine("disabled: " + e.Message); } }
try { if (File.Exists(disabledPath)) table.SetDisabled(System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(disabledPath)) ?? Array.Empty<string>()); }
catch (Exception e) { Console.WriteLine("disabled: " + e.Message); }
string lanesData = Path.Combine(root, "data", "lanes");
string lanesBoard = Path.Combine(lanesData, "lanes_leaderboard.json");
string partyData = Path.Combine(root, "data", "party");
// Deck manager: which playlists the virtual deck draws from (data/decks.json; missing = all)
var decks = DeckCatalog.Load(Path.Combine(root, "data"), table.AllCards);
Loc.Load(Path.Combine(root, "data"));
GameTables.Load(Path.Combine(root, "data"));                 // the original's tables, imported at setup
GameTables.Localize = k => Loc.Get(k);
Views.Print = DeckCatalog.LoadPrintInfo(Path.Combine(root, "data"));
string decksFile = Path.Combine(root, "data", "decks.json");
if (DeckCatalog.LoadActive(decksFile) is { } savedDecks && !table.SetActiveDecks(savedDecks).Ok)
    Console.WriteLine("decks: saved selection is too small — using every deck");
table.SetMode(opt.Mode, new Dictionary<string, string> { ["hand"] = opt.Hand.ToString(), ["leaderboard"] = lanesBoard, ["partyData"] = partyData, ["lanesData"] = lanesData, ["mixgridData"] = Path.Combine(root, "data", "mixgrid") });
Views.ModeId = () => table.Mode.Id;
Console.WriteLine($"cards: {db.Count}   deck: {table.Snap().DeckCount}   mode: {table.Mode.Title}");

// ---------- audio: a mix director applies the original's tempo/key rules ----------
// the port's team decks = the team icons on table and phones (Red = Fever, Blue = Ouroboros); used for the VO names
// team deck → the announcer's name for it (vo_p1_<Name>N.wav: "theblade" → "TheBlade"); chosen on Icon Select
var voNames = Directory.Exists(Path.Combine(root, "assets_raw", "fmod", "master"))
    ? Directory.GetFiles(Path.Combine(root, "assets_raw", "fmod", "master"), "vo_p1_*.wav").Select(f => Path.GetFileNameWithoutExtension(f)[6..].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'))
        .Distinct().ToDictionary(n => n.ToLowerInvariant(), n => n)
    : new Dictionary<string, string>();
string TeamDeck(string side)
{
    string? id = table.Mode is ClashMode cm ? cm.TeamIcon(side == "Red" ? Side.Red : Side.Blue) : null;
    return id != null && voNames.TryGetValue(id, out var n) ? n : side == "Red" ? "Fever" : "Ouroboros";
}
MixEngine? mix = null;
MixPlayer? player = null;
MixDirector? director = null;
double[]? mixVolumesBefore = null;                  // studio levels saved while a mix plays back
VuMeters? vu = null;
var padQueue = Channel.CreateUnbounded<int>();
if (!opt.NoAudio && GameTables.Audio == null)
    Console.WriteLine($"audio: off — the game's audio settings aren't imported ({GameTables.Error}); run setup again");
else if (!opt.NoAudio)
{
    mix = new MixEngine(24000);
    try { player = new MixPlayer(mix); player.Start(); Console.WriteLine("audio: playing on default output"); }
    catch (Exception ex) { Console.WriteLine($"audio: no output device ({ex.Message}) — mix still tracked"); }
    director = new MixDirector(mix, audioDir);
    vu = new VuMeters(mix); vu.Start(CancellationToken.None);
    var clockMix = mix;
    table.BeatClock = () => (clockMix.BeatPosition, clockMix.PlayBpm);
    director.Log += Console.WriteLine;
    var d0 = director; Views.MixState = () => d0.State();
    director.TempoCreep = table.Mode.Id != "freestyle";
    table.PadChanged += (pad, _) => padQueue.Writer.TryWrite(pad);
    table.Music += cmd =>
    {
        if (cmd == "freestyle_button") director.RandomTransition();
        else if (cmd == "party_round") director.RandomTransition("round_trans");
        else if (cmd == "lanes_finale") director.Finale = true;
        else if (cmd is "lanes_start" or "lanes_end") director.Finale = false;
        else if (cmd.StartsWith("sfx:")) director.PlaySfx(cmd[4..]);
        else if (cmd.StartsWith("keysfx:")) director.KeySfx(cmd[7..]);
        else if (cmd.StartsWith("score:")) director.QueueScore(int.Parse(cmd[6..]));
        else if (cmd.StartsWith("wheel_spin:")) director.WheelSpin(double.Parse(cmd[11..], System.Globalization.CultureInfo.InvariantCulture));
        else if (cmd == "wheel_up") director.WheelUp();
        else if (cmd == "wheel_down") director.WheelDown();
        else if (cmd == "wheel_stop") director.WheelStop();
        else if (cmd.StartsWith("outro:")) director.Outro(cmd[6..]);
        else if (cmd == "lobby_join") director.LobbyJoin();
        else if (cmd == "intro:clash") director.ClashIntro(TeamDeck("Red"), TeamDeck("Blue"));     // team A = side 2 (Red)
        else if (cmd.StartsWith("winvo:")) director.WinVo(TeamDeck(cmd[6..]));
        else if (cmd == "party_start") director.PartyIntro();
        else if (cmd == "lanes_start") director.PuzzleIntro();
        else if (cmd == "turnend") director.TurnEnd();
        else if (cmd.StartsWith("badge:")) { }                    // handled by the badge store (below)
        else if (cmd == "card_removed") { director.UiSfx("card_removed_scratch"); director.UiSfx("card_removed_fall"); }
        else if (cmd.StartsWith("fxdsp:"))
        {   // fxdsp:<effect>:<fx slot>:<fx cid>:<target,target>
            var f = cmd.Split(':');
            if (f.Length == 5) director.FxEffect(f[1], int.Parse(f[2]), f[3], f[4].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse));
        }
    };
    _ = Task.Run(async () =>
    {
        // one worker; it reads each slot's CURRENT card when it gets to it, so it always converges
        await foreach (int pad in padQueue.Reader.ReadAllAsync())
        {
            try { var (c, parts, sil) = table.Sounding(pad); director.OnSlot(pad, c, parts, sil); }
            catch (Exception ex) { Console.WriteLine($"audio: slot {pad + 1} failed: {ex.Message}"); }
        }
    });
    // music clock: tempo creep outside Freestyle; push mix state (tempo/key/bar) to screens
    _ = Task.Run(async () =>
    {
        string last = "";
        while (true)
        {
            await Task.Delay(100);
            try
            {
                bool playing = table.Mode.Id == "mixplay";
                director.TempoCreep = table.Mode.Id != "freestyle" && !playing;
                director.PinMaster = playing;
                director.SlotFxEnabled = table.Mode.Id != "freestyle";       // studio mode has no per-slot FX DSP
                if (!playing && mixVolumesBefore != null)
                {   // leaving mix playback: the studio levels come back (the original's audio reads the viewer's only in state 9)
                    for (int i = 0; i < 5; i++) director.SetSlotVolume(i, mixVolumesBefore[i]);
                    mixVolumesBefore = null;
                }
                if (table.Mode.Id != "lanes") director.Finale = false;
                var (inGame, progress) = table.Crowd();
                director.SetGame(table.Mode.Id, inGame, progress);
                director.Tick();
                table.MusicBpm = mix.PlayBpm;
                var now = JsonSerializer.Serialize(director.State());
                if (now != last) { last = now; table.Touch(); }
            }
            catch (Exception ex) { Console.WriteLine("music: " + ex.Message); }
        }
    });
}

// ---------- physical board (optional) ----------
// Scans until the board appears, polls it, and reconnects if the link drops. Status is shown on the table.
CancellationTokenSource boardCts = new();
var boardStatus = new BoardStatus { Enabled = opt.Board };
var batteryMon = new BoardBattery();
CancellationTokenSource? forceOffCts = null;
bool ignoreRssiNext = false;                                // the connect panel's retry: ignore the RSSI threshold once
Views.BoardState = () => boardStatus;
var boardOn = new List<(int Slot, Card Card)>();           // cards the board has reported
IReadOnlyList<Cti>[]? boardStacks = null;
var unknownCards = new Dictionary<int, Card>();            // placeholders for CTIs the database doesn't know
var unknownAt = new DateTime?[5];
bool deckBuilderOpen = false;                               // Menu_DeckBuilder: stack-only mode, cards are counted, not played
var cardContent = new CardContent(root) { Changed = () => table.Touch() };
bool claimOpen = false;                                     // Menu_CollectionClaim: stack-only too; the board's UIDs are read
var claim = new ClaimState();
BoardLights.Override = () => deckBuilderOpen || claimOpen   // CardRegistration lights: slots with cards Full, else Off; owned slots tinted
    ? new LightCue(LightScene.CardRegistration, Side.None, Enumerable.Range(0, 5).Select(i => { lock (boardOn) return boardOn.Any(x => x.Slot == i); }).ToArray(),
        Owned: claimOpen ? claim.OwnedSlots() : null)
    : null;                           // CardSlots.OnUnknownCTI: when an unknown card landed in each slot
void UnknownChanged(int slot, Card card, bool added)
{   // UnknownCardError: the slot shows an error button once it has held an unknown card for > 2 s
    if (!card.Cid.StartsWith("unknown_") || slot is < 0 or > 4) return;
    lock (unknownAt) unknownAt[slot] = added ? DateTime.UtcNow : null;
    void Refresh() { lock (unknownAt) boardStatus.UnknownSlots = Enumerable.Range(0, 5).Where(i => unknownAt[i] is DateTime t && DateTime.UtcNow - t > TimeSpan.FromSeconds(2)).ToArray(); table.Touch(); }
    Refresh();
    if (added) _ = Task.Delay(2100).ContinueWith(_ => Refresh());
}                   // tracker contents kept across a link loss
DropMixBoard? liveBoard = null; bool pausedByBoard = false;
table.BoardCardCount = () => liveBoard?.CardCount ?? boardStacks?.Sum(x => x.Count) ?? 0;
if (opt.Board)
{
    _ = Task.Run(async () =>
    {
        while (!boardCts.IsCancellationRequested)
        {
            await using var board = new DropMixBoard();
            board.Log += m => Console.WriteLine($"board: {m}");
            if (opt.BoardTrace) { board.Trace = m => Console.WriteLine($"board~ {DateTime.Now:HH:mm:ss.fff} {m}"); BoardLights.Trace = board.Trace; }
            board.CardChanged += e =>
            {
                Console.WriteLine($"board: {(e.Added ? "placed" : "lifted")} CTI {e.Cti.Value} on slot {(int)e.Slot + 1} (stack {e.StackCount})");
                var card = db.Resolve(e.Cti);
                if (card == null)
                {   // not in the database: a placeholder so Clash can show CARD NOT RECOGNIZED until it's lifted
                    Console.WriteLine($"board: unknown CTI {e.Cti.Value}");
                    lock (unknownCards) if (!unknownCards.TryGetValue(e.Cti.Value, out card))
                        unknownCards[e.Cti.Value] = card = new Card { Cid = $"unknown_{e.Cti.Value}", Cti = (ushort)e.Cti.Value, Name = "Unknown card" };
                }
                lock (boardOn)
                {
                    if (e.Added) boardOn.Add(((int)e.Slot, card));
                    else { int i = boardOn.FindLastIndex(x => x.Slot == (int)e.Slot && ReferenceEquals(x.Card, card)); if (i >= 0) boardOn.RemoveAt(i); }
                }
                if (!deckBuilderOpen && !claimOpen) table.BoardCard((int)e.Slot, card, e.Added); else table.Touch();
                claim.Dirty = true;
                UnknownChanged((int)e.Slot, card, e.Added);
            };
            board.ButtonPressed += b =>
            {
                if (b != BoardButton.Eq) return;              // the DropMix button on the board's edge
                var r = table.Button(null);
                Console.WriteLine($"board: DropMix button{(r.Ok ? "" : " — " + r.Error)}");
            };
            board.BatteryLevelChanged += p =>
            {
                boardStatus.Battery = p;
                int slots = board.Contents().Count(c => c.Count > 0);
                batteryMon.Add(p, slots);
                boardStatus.BatteryLevel = Math.Round(batteryMon.Level, 3); boardStatus.BatteryLow = batteryMon.Low; boardStatus.BatteryCritical = batteryMon.Critical;
                if (batteryMon.ForceDisconnect)
                {   // BluetoothBoardReader: OK popup "...disconnected due to low battery level..." then Disconnect
                    Console.WriteLine($"board: battery {p}% with {slots} slots in use — disconnecting (low battery)");
                    boardStatus.ForcedOff = true; forceOffCts?.Cancel();
                }
                table.Touch();
            };
            using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(boardCts.Token);
            forceOffCts = linkCts;
            board.Disconnected += () => { boardStatus.State = "lost"; linkCts.Cancel(); };

            // Scan() refuses while ForceDisconnect stands; 40 s without readings clears it
            while (batteryMon.ForceDisconnect && !boardCts.IsCancellationRequested) { batteryMon.ExpireIfIdle(); await Task.Delay(1000); }
            board.IgnoreRssi = ignoreRssiNext;
            board.OutOfRange += rssi => { if (boardStatus.State != "outofrange") { boardStatus.State = "outofrange"; table.Touch(); Console.WriteLine($"board: heard at {rssi} dBm — too far (threshold {board.RssiThreshold})"); } };
            boardStatus.State = "scanning"; table.Touch();
            bool ok;
            try { ok = await board.ConnectAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception ex) { Console.WriteLine($"board: connect failed: {ex.Message}"); ok = false; }
            if (!ok)
            {   // BoardConnectPanel: Failed ("ERROR CONNECTING… TAP TO RETRY") keeps showing until a retry; out of range likewise
                if (board.ConnectFailed) boardStatus.State = "failed";
                else if (boardStatus.State != "outofrange") boardStatus.State = "searching";
                table.Touch(); await Task.Delay(2000); continue;
            }
            ignoreRssiNext = false;

            // reconnect: start from what was on the board, so only real changes register; resume a paused game
            if (boardStacks != null) board.Seed(boardStacks);
            liveBoard = board;
            if (pausedByBoard) { table.Paused = false; pausedByBoard = false; Console.WriteLine("board: reconnected, game resumed"); }
            // the app suppresses card-count requests while the wheel / coin flip / Clash start play
            board.SuspendPolling = () => table.Lights().Scene is LightScene.Spinner or LightScene.ClashStart;
            boardStatus.ForcedOff = false;
            boardStatus.State = "connected"; table.Touch(); boardStatus.Name = board.DeviceName; boardStatus.Firmware = board.FirmwareVersion;
            Console.WriteLine($"board: connected to {board.DeviceName}");
            try { await board.ReadBatteryPercentAsync(); } catch { }
            var battery = Task.Run(async () =>
            {
                while (!linkCts.IsCancellationRequested)
                {
                    try { await Task.Delay(10000, linkCts.Token); await board.ReadBatteryPercentAsync(); } catch { }
                }
            });
            var lights = Task.Run(async () =>
            {
                try { await new BoardLights(board, table, mix, Path.Combine(root, "assets_raw", "meters")).RunAsync(linkCts.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.WriteLine($"board: lights stopped: {ex.Message}"); }
            });
            await board.RunPollLoopAsync(TimeSpan.FromMilliseconds(300), linkCts.Token);   // ≈ the app's 0.53 s poll period
            // link lost: keep the table as it is (the original pauses rather than clearing the mix) and remember the
            // board's contents to seed the next connection; pause a running game until the board is back
            boardStacks = board.Contents(); liveBoard = null;
            if (!boardCts.IsCancellationRequested && table.Crowd().InGame && !table.Paused)
            { table.Paused = true; pausedByBoard = true; Console.WriteLine("board: link lost, game paused"); }
            if (!boardCts.IsCancellationRequested) { Console.WriteLine("board: reconnecting..."); await Task.Delay(1000); }
        }
    });
}

// ---------- rules clock: orphan timeouts, turn timers, wheel, clears ----------
_ = Task.Run(async () => { while (true) { await Task.Delay(100); try { table.Tick(); } catch (Exception ex) { Console.WriteLine("tick: " + ex.Message); } } });

// ---------- web server ----------
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = ContentPaths.WebRoot() });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://*:{opt.Port}");   // IPv4 + IPv6 (localhost resolves to ::1 first)
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
var app = builder.Build();
HostGuard.Use(app, allowPhoneRoutes: true);
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });  // phones always revalidate after an update

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

app.MapGet("/api/players", () => table.Snap().Players);

// Host-only (needs the window token; not a phone route): remove a player from the table.
app.MapPost("/api/kick", (KickRequest req) =>
    Results.Ok(new { ok = !string.IsNullOrWhiteSpace(req.Name) && table.RemovePlayer(req.Name!) }));

app.MapPost("/api/join", (JoinRequest req) =>
{
    var r = table.Join(req.Name ?? "", req.Token);
    return r.Outcome switch
    {
        JoinOutcome.InvalidName => Results.BadRequest(new { error = "Pick a name (1–24 characters)." }),
        JoinOutcome.NameInUse => Results.Conflict(new { error = $"\"{r.Name}\" is playing on another device right now." }),
        _ => Results.Ok(new { token = r.Token, name = r.Name, outcome = r.Outcome.ToString() }),
    };
});

IResult R(string? token, ActionResult r) =>
    r.Ok ? Results.Ok() : table.ByToken(token) == null && token != null ? Results.Unauthorized() : Results.BadRequest(new { error = r.Error });

app.MapPost("/api/play", (PlayRequest req) => R(req.Token, table.Play(req.Token ?? "", req.Cid ?? "", req.Pad)));
app.MapPost("/api/draw", (TokenRequest req) => R(req.Token, table.Draw(req.Token ?? "")));
app.MapPost("/api/clear", (PadRequest req) => R(req.Token, table.ClearPad(req.Token ?? "", req.Pad)));
app.MapPost("/api/undo", (TokenRequest req) => R(req.Token, table.Undo(req.Token ?? "")));
app.MapPost("/api/team", (TeamRequest req) =>
    R(req.Token, table.SetTeam(req.Token ?? "", Enum.TryParse<Side>(req.Team, true, out var s) ? s : Side.None)));
app.MapPost("/api/action", (ModeActionRequest req) => R(req.Token, table.ModeAction(req.Token ?? "", req.Action ?? "", req.Arg)));
// The DropMix button: phones (token) or the big-screen button (no token)
app.MapPost("/api/button", (TokenRequest? req) => R(req?.Token, table.Button(req?.Token)));

// Host (big-screen) controls: choose mode, start, options
// ---- badges (badges01.txt; progress in data/badges.json) ----
var collection = new CardCollection(Path.Combine(root, "data"));
// Portable verified-cards save: <install folder>\saves\verified-cards.json mirrors the collection (each card's UID and
// its board signature). Copy it into the same folder on another of your PCs and OpenMix there adds those cards at
// start, checking every signature again and working out each card from its UID, so that PC passes Add to Collection
// without the board.
collection.UseSaveFile(Path.Combine(AppContext.BaseDirectory, "saves", "verified-cards.json"),
    uid => db.Resolve(BoardProtocol.CtiFromUid(uid)) is Card c ? (c.Cid, c.Icon) : null);

// The virtual deck only draws cards the player really has: scanned into the collection, or with hand-imported audio
// (those count as scanned, just awaiting their card data). Physical board cards are always playable regardless.
HashSet<string> AvailableCids()
{
    var set = collection.OwnedCids();                                  // scanned-in cards
    foreach (var c in db.Cards)
        if (HasPreview(c)) set.Add(c.Cid);                             // + cards whose audio was hand-imported (playable samples present)
    return set;
}
void RefreshAvailable() => table.SetAvailableCards(AvailableCids());
RefreshAvailable();
// Ownership gate: until the user has added at least one signature-checked card on their board, OpenMix opens on
// the Add to Collection screen (--no-gate for development).
Views.GateState = () => new { needCards = false, cards = cardContent.View() };   // every card counts as owned: no scan needed to start
var simUids = new List<(int Slot, Card Card, ulong Uid)>(); long simSerial = 0;
var badges = new BadgeStore(Path.Combine(root, "data"), cid => db.ResolveCid(cid) is Card bc ? (bc.Name, bc.ImageName) : null);
badges.Unlocked += u => { Console.WriteLine($"badge: {u.Title} — {u.Text}"); director?.UiSfx("achieve_ping"); table.Touch(); };
Views.BadgeState = () => badges.Recent();
// global badges when a card lands: played in the opposite key mode, or below/above a tempo
// ---- the interactive introduction (Walkthrough): voiced steps over Freestyle; VO from assets_raw/fmod/ftue by language ----
var ftueDir = Path.Combine(root, "assets_raw", "fmod", "ftue");
string? FtueFile(string ev)
{   // ftue_<LANG>_<event>.wav; a few files carry a suffix or a stray space ("001_welcome .wav", "001_welcome_alt3.wav")
    string lang = Loc.Language switch
    {
        "fr" => "FRE", "de" => "GER", "it" => "ITA", "es-es" => "SPA", "es-mx" => "LAT", "nl" => "DUT", "fi" => "FIN",
        "no" => "NOR", "pl" => "POL", "pt-pt" => "POR", "pt-br" => "BRA", "sv" => "SWE", _ => "ENG",
    };
    if (!Directory.Exists(ftueDir)) return null;
    foreach (var l in new[] { lang, "ENG" })
    {
        var exact = Path.Combine(ftueDir, $"ftue_{l}_{ev}.wav");
        if (File.Exists(exact)) return exact;
        var near = Directory.GetFiles(ftueDir, $"ftue_{l}_{ev}*.wav").FirstOrDefault()
                   ?? Directory.GetFiles(ftueDir, $"ftue_{l}_{ev.Split('_')[0]}_*.wav").FirstOrDefault();   // 020_stacking vs 021_stacking_request
        if (near != null) return near;
    }
    return null;
}
var walkthrough = new WalkthroughRunner(Path.Combine(root, "data"),
    i =>
    {   // the table's pad, or the physical cards still on the board (a mode change empties the pads, not the board)
        var c = table.Top(i); int n = table.Snap().Pads[i].StackCount;
        List<Card> phys; lock (boardOn) phys = boardOn.Where(x => x.Slot == i).Select(x => x.Card).ToList();
        return (c?.Type ?? phys.LastOrDefault()?.Type, Math.Max(n, phys.Count));
    },
    ev =>
    {
        var f = FtueFile(ev);
        if (f == null) { Console.WriteLine($"walkthrough: no VO for {ev}"); return 2; }
        if (director != null) return director.PlayVoice(Path.GetRelativePath(Path.Combine(root, "assets_raw", "fmod", "master"), f)[..^4], "ftue");
        try { var s = SampleCache.DecodeWav(f); return s.Frames / (double)s.SampleRate; } catch { return 2; }   // no audio: keep the timing
    },
    () => director?.StopVoice("ftue"));
Views.WalkState = () => walkthrough.View();
_ = Task.Run(async () => { long last = -1; while (true) { await Task.Delay(100); walkthrough.Tick(); if (walkthrough.Active || last != 0) { var v = walkthrough.View(); string j = System.Text.Json.JsonSerializer.Serialize(v); long h = j.GetHashCode(); if (h != last) { last = walkthrough.Active ? h : 0; table.Touch(); } } } });
table.PadChanged += (pad, c) => { if (c != null && pad is >= 0 and < 5) walkthrough.CardPlaced(pad, c); };
app.MapPost("/api/walkthrough/start", () =>
{
    var r = table.SetMode("freestyle", new Dictionary<string, string>());
    if (!r.Ok) return Results.BadRequest(new { error = r.Error });
    walkthrough.Start(); return Results.Ok();
});
var walkOfferFile = Path.Combine(root, "data", "walkthrough_offered");   // the first-launch offer is made once
app.MapGet("/api/walkthrough/offer", () => new { offered = File.Exists(walkOfferFile) });
app.MapPost("/api/walkthrough/offer", () => { try { File.WriteAllText(walkOfferFile, DateTime.Now.ToString("O")); } catch { } return Results.Ok(); });
app.MapGet("/api/walkthrough", () => walkthrough.View() ?? new { stage = "off" });
app.MapPost("/api/walkthrough/skip", () => { walkthrough.Skip(); table.Touch(); return Results.Ok(); });
app.MapPost("/api/walkthrough/popup", () => { walkthrough.PopupClosed(); return Results.Ok(); });
app.MapPost("/api/walkthrough/testsound", () => { director?.UiSfx("button_select"); return Results.Ok(); });   // the setup popup's Test Sound

table.PadChanged += (pad, c) =>
{
    if (c == null || director == null || table.Mode.Id is "mixplay") return;
    var (tempo, _, minor) = director.Master;
    string own = c.Mode.ToString();
    if (minor is bool mn && ((own == "Major" && mn) || (own == "Minor" && !mn))) badges.Track("global_playCardInOppositeMode", c.Cid, c.Icon, cardMode: own);
    badges.Track("global_playCardBelowTempo", c.Cid, c.Icon, tempo);
    badges.Track("global_playCardAboveTempo", c.Cid, c.Icon, tempo);
};
table.Music += cmd =>
{   // badge:<tracker>:<cid>:<measured>:<amount> from the modes
    if (!cmd.StartsWith("badge:")) return;
    var f = cmd.Split(':');
    if (f.Length < 5) return;
    var bc = db.ResolveCid(f[2]);
    badges.Track(f[1], f[2], bc?.Icon, double.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mv) ? mv : 0,
        int.TryParse(f[4], out var am) ? am : 1, bc?.Mode.ToString());
};
app.MapGet("/api/badges", () => badges.View(cid => db.ResolveCid(cid)?.Mode.ToString()));

// remembered game options (the original's PlayerPrefs: Clash points/timer, Party difficulty, Puzzle Normal/Fever)
string prefsFile = Path.Combine(root, "data", "mode_prefs.json");
var modePrefs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
try { if (File.Exists(prefsFile)) modePrefs = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(prefsFile)) ?? modePrefs; } catch { }
void RememberPref(string mode, string key, string value)
{
    if (!modePrefs.TryGetValue(mode, out var d)) modePrefs[mode] = d = new();
    d[key] = value;
    try { File.WriteAllText(prefsFile, JsonSerializer.Serialize(modePrefs)); } catch { }
}

app.MapPost("/api/mode", (ModeRequest req) =>
{
    string modeName = (req.Mode ?? "freestyle").ToLowerInvariant();
    var o = new Dictionary<string, string>(modePrefs.TryGetValue(modeName, out var saved) ? saved : new Dictionary<string, string>());
    foreach (var kv in req.Options ?? new Dictionary<string, string>()) o[kv.Key] = kv.Value;
    o["leaderboard"] = lanesBoard; o["lanesData"] = lanesData; o["partyData"] = partyData; o["mixgridData"] = Path.Combine(root, "data", "mixgrid");
    var r = table.SetMode(modeName, o);
    if (r.Ok && walkthrough.Active) walkthrough.Skip();         // leaving Freestyle ends the introduction
    return r.Ok ? Results.Ok() : Results.BadRequest(new { error = r.Error });
});
// Freestyle studio panel: tempo, key (circle of fifths), mode, per-slot volume
app.MapPost("/api/studio", (StudioRequest req) =>
{
    if (director == null) return Results.BadRequest(new { error = "Audio is off." });
    if (table.Mode.Id != "freestyle") return Results.BadRequest(new { error = "The studio controls are for Freestyle." });
    switch (req.Action)
    {
        case "open": director.SetStudioOpen(req.Value is null || req.Value > 0); break;
        case "tempo": director.SetTempo(req.Value ?? 120); break;
        case "tempoStep": director.SetTempo((mix?.PlayBpm ?? 120) + (req.Value ?? 1)); break;
        case "key": director.StepKey((int)(req.Value ?? 1)); break;
        case "mode": director.ToggleMode(); break;
        case "volume": director.SetSlotVolume(req.Slot ?? 0, req.Value ?? 0.8); break;
        default: return Results.BadRequest(new { error = "Unknown studio action." });
    }
    table.Touch();
    return Results.Ok();
});

// Settings: music and effects volume, saved in data/settings.json
string settingsFile = Path.Combine(root, "data", "settings.json");
double musicVol = 1, fxVol = 1, crowdVol = 1; bool reminders = true; string language = "en";
string artDownload = "";   // card-art download scope: "all" | "collection" | "" (unset -> first-run prompt asks)
try
{
    if (File.Exists(settingsFile))
    {
        using var sd = JsonDocument.Parse(File.ReadAllText(settingsFile));
        if (sd.RootElement.TryGetProperty("music", out var mv)) musicVol = Math.Clamp(mv.GetDouble(), 0, 1);
        if (sd.RootElement.TryGetProperty("effects", out var ev)) fxVol = Math.Clamp(ev.GetDouble(), 0, 1);
        if (sd.RootElement.TryGetProperty("crowd", out var cv)) crowdVol = Math.Clamp(cv.GetDouble(), 0, 1);
        if (sd.RootElement.TryGetProperty("reminders", out var rv)) reminders = rv.GetBoolean();
        if (sd.RootElement.TryGetProperty("language", out var lv)) language = lv.GetString() ?? "en";
        if (sd.RootElement.TryGetProperty("artDownload", out var dv)) artDownload = dv.GetString() ?? "";
    }
}
catch { }
void ApplySettings()
{
    if (mix != null) mix.MusicGain = (float)(musicVol * musicVol);          // slider → perceived loudness
    Loc.Language = language;
    if (director != null) { director.SfxGain = 0.6 * fxVol * fxVol; director.CrowdGain = crowdVol * crowdVol; }   // 0.6 = the engine's default level
}
ApplySettings();
cardContent.Scope = artDownload == "collection" ? "collection" : "all";
cardContent.ScopeChosen = artDownload is "all" or "collection";
if (cardContent.ScopeChosen) cardContent.AutoPull();   // on launch: background-pull missing art for the chosen scope
// a card placed on the board whose content (art or the unencrypted audio) isn't downloaded yet: fetch it on demand
table.PadChanged += (pad, c) =>
{
    if (c == null) return;
    bool hasArt = c.ImageName.Length == 0 || File.Exists(Path.Combine(root, "cardassets", "card_png", c.ImageName + ".png")) || File.Exists(Path.Combine(root, "cardassets", "card_thumb", c.ImageName + ".jpg"));
    bool hasAudio = c.AudioId.Length == 0 || Directory.Exists(Path.Combine(audioDir, c.AudioId));
    if (hasArt && hasAudio) return;
    cardContent.QueueArt(new[] { (int)c.Cti });
};
object SettingsView() => new { music = musicVol, effects = fxVol, crowd = crowdVol, reminders, language, artDownload, languages = Loc.Languages, audio = mix != null, board = opt.Board, version = "OpenMix " + (typeof(ContentPaths).Assembly.GetName().Version?.ToString(3) ?? "0.1.0") + " (unofficial; plays content imported from DropMix 1.9.0)" };
app.MapGet("/api/settings", () => SettingsView());
// Pause popup: hold the mode timers while it is open
app.MapPost("/api/pause", (PauseRequest req) => { table.Paused = req.On; table.Touch(); return Results.Ok(new { paused = table.Paused }); });
app.MapPost("/api/settings", (SettingsRequest req) =>
{
    if (req.Music is double m) musicVol = Math.Clamp(m, 0, 1);
    if (req.Effects is double f) fxVol = Math.Clamp(f, 0, 1);
    if (req.Crowd is double c) crowdVol = Math.Clamp(c, 0, 1);
    if (req.Reminders is bool r) reminders = r;
    if (req.ArtDownload is "all" or "collection") { artDownload = req.ArtDownload; cardContent.Scope = artDownload == "collection" ? "collection" : "all"; cardContent.ScopeChosen = true; cardContent.AutoPull(); table.Touch(); }
    if (req.Language is string lg && Loc.Languages.Contains(lg)) { language = lg; table.Touch(); }
    ApplySettings();
    try { File.WriteAllText(settingsFile, JsonSerializer.Serialize(new { music = musicVol, effects = fxVol, crowd = crowdVol, reminders, language, artDownload })); } catch { }
    return Results.Ok(SettingsView());
});

// the original's text table (13 languages) and the Guide (TutorialViewer + TutorialSlides prefabs, data/guide.json)
app.MapGet("/api/loc/{lang}", (string lang) => Loc.Table(lang == "current" ? Loc.Language : lang));
// imported values the screens need (wheel odds and tints, coin-flip curve, VU bloom, deck-builder rules, saved mixes)
app.MapGet("/api/tables", () =>
{
    double[]? coin = null;
    try
    {
        var f = Path.Combine(root, "assets_raw", "obb_ui", "wheel_coinflip_curve.json");
        if (File.Exists(f)) { using var d = JsonDocument.Parse(File.ReadAllBytes(f)); coin = d.RootElement.GetProperty("table").EnumerateArray().Select(x => x.GetDouble()).ToArray(); }
    }
    catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { Console.WriteLine($"coin curve: {ex.Message}"); }
    var cr = GameTables.Clash;
    return new
    {
        loaded = GameTables.Loaded,
        wheel = GameTables.WheelSlices.Select(x => new { value = x.Value, weight = x.Weight }),
        wheelColours = GameTables.WheelColours.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.r, kv.Value.g, kv.Value.b }),
        coinCurve = coin,
        vuBloom = GameTables.VuBloom,
        deckRules = cr == null ? null : new { size = cr.DeckSize, budget = cr.PowerBudget, maxCopies = cr.MaxDuplicates },
        snapshotMinCards = GameTables.Snapshots?.MinRequiredCards,
        loopBars = GameTables.Audio?.LoopLengthInBars ?? 0,
        startTempo = GameTables.Audio?.StartingTempo,
        uiColours = GameTables.UiColours.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.r, kv.Value.g, kv.Value.b }),
    };
});
app.MapGet("/api/guide", () => { var f = Path.Combine(root, "data", "guide.json"); return File.Exists(f) ? Results.Text(File.ReadAllText(f), "application/json") : Results.NotFound(); });

// Settings → CREDITS / EULA / PRIVACY POLICY: the app's own TextAssets (data/text, local only), <font=…> tags as headings
app.MapGet("/api/text/{name}", (string name) =>
{
    if (OpenMixTexts.Get(name) is { } own) return Results.Text(own, "text/plain; charset=utf-8");
    if (name is not ("credits" or "eula" or "privacy")) return Results.NotFound();
    // the original game's own documents, imported from the user's copy: shown for reference, labelled as historical
    var f = Path.Combine(root, "data", "text", name + ".txt");
    return File.Exists(f) ? Results.Text(File.ReadAllText(f), "text/plain; charset=utf-8") : Results.NotFound();
});

// Collection browser: the whole catalog, grouped by deck, every card owned
app.MapGet("/api/catalog", () =>
{
    var meta = decks.Decks.ToDictionary(d => d.Icon, d => d);
    var groups = table.AllCards.Where(c => c.Type.ToString() != "Invalid").GroupBy(c => c.Icon)
        .Select(g =>
        {
            meta.TryGetValue(g.Key, out var d);
            return new
            {
                icon = g.Key, name = d?.Name ?? g.Key, kind = d?.Kind ?? "", season = d?.Season ?? 0,
                cards = g.OrderBy(c => c.Cid).Select(c => new
                {
                    cid = c.Cid, name = c.Name, artist = c.Artist, type = c.Type.ToString().ToLowerInvariant(),
                    power = c.Power, image = c.ImageName, tempo = (int)c.Tempo,
                    key = c.Key.ToString(), mode = c.Mode.ToString(), ability = c.Ability, instruments = c.Instruments,
                    audio = HasPreview(c), disabled = table.IsDisabled(c.Cid),
                }).ToList(),
            };
        }).OrderBy(g => g.season).ThenBy(g => g.name).ToList();
    return new { decks = groups, total = groups.Sum(g => g.cards.Count) };
});

// ---- card sound preview: render the card's own phrase offline (its tempo/key, so no transpose or stretch) ----
var previewCache = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
string? PreviewDir(string audioId)   // tolerate both the double-nested <id>/<id>/ and flat <id>/ layouts
{
    if (string.IsNullOrEmpty(audioId)) return null;
    foreach (var dir in new[] { Path.Combine(audioDir, audioId, audioId), Path.Combine(audioDir, audioId) })
        if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.midisong").Any()
            && Directory.Exists(Path.Combine(dir, "samples")) && Directory.EnumerateFiles(Path.Combine(dir, "samples")).Any())
            return dir;
    return null;
}
bool HasPreview(Card c) => PreviewDir(c.AudioId) != null;
byte[]? RenderPreview(Card c)
{
    if (!HasPreview(c)) return null;
    return previewCache.GetOrAdd(c.Cid, _ =>
    {
        const int rate = 24000;
        var m = new MixEngine(rate);
        var dir = new MixDirector(m, audioDir) { PinMaster = true };
        if (c.Key != MusicKey.None) dir.SetKey((int)c.Key, c.Mode == MusicMode.Minor);   // the card's own key -> no transpose
        dir.SetTempo(c.Tempo > 0 ? c.Tempo : 120);                                        // its own tempo -> no time-stretch
        dir.SetSlotVolume(0, 0.85);
        dir.OnSlot(0, c, Rules.AudioParts(c, 0), false);
        m.WaitIdle();                                                                     // offline: let the render land
        var outBuf = new List<float>(); var chunk = new float[2400];
        int want = rate * 6 * 2;                                                          // ~6 seconds, stereo
        while (outBuf.Count < want) { m.Read(chunk, 0, chunk.Length); outBuf.AddRange(chunk); }
        var pcm = outBuf.ToArray();
        int fin = Math.Min(pcm.Length / 2, rate / 50), fade = Math.Min(pcm.Length / 2, rate);   // ~20 ms in, 1 s out
        for (int f = 0; f < fin; f++) { float g = (float)f / fin; pcm[f * 2] *= g; pcm[f * 2 + 1] *= g; }
        for (int f = 0; f < fade; f++) { float g = 1 - (float)f / fade; int at = pcm.Length / 2 - fade + f; pcm[at * 2] *= g; pcm[at * 2 + 1] *= g; }
        return WavWriter.Stereo16Bytes(pcm, rate);
    });
}
app.MapGet("/api/preview/{cid}", async (string cid) =>
{
    var c = db.ResolveCid(cid);
    if (c == null) return Results.NotFound();
    var bytes = await Task.Run(() => RenderPreview(c));
    return bytes == null ? Results.NotFound() : Results.File(bytes, "audio/wav");
});

// ---- disable a card from being shuffled into the virtual deck (the collection toggle) ----
app.MapPost("/api/card/disable", (CardDisableRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Cid) || db.ResolveCid(req.Cid) == null) return Results.BadRequest(new { error = "Unknown card." });
    table.SetCardEnabled(req.Cid!, req.Enabled);
    SaveDisabled();
    return Results.Ok(new { cid = req.Cid, disabled = !req.Enabled });
});

// Deck manager
app.MapGet("/api/decks", () => DecksView());
app.MapPost("/api/decks", (DecksRequest req) =>
{
    var known = decks.Decks.Select(d => d.Icon).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var cur = (table.ActiveDecks ?? known).ToHashSet(StringComparer.OrdinalIgnoreCase);
    IEnumerable<string>? next = req.Preset switch
    {
        "all" => null,
        "starter" => decks.Decks.Where(d => d.Kind == "Starter").Select(d => d.Icon),
        "season1" => decks.Decks.Where(d => d.Season == 1).Select(d => d.Icon),
        "season2" => decks.Decks.Where(d => d.Season == 2 || d.Kind == "Starter").Select(d => d.Icon),
        null or "" => null,
        _ => Array.Empty<string>(),
    };
    if (req.Preset is null or "")
    {
        if (req.Active != null) cur = req.Active.Where(known.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var icon in req.Toggle ?? Array.Empty<string>())
            if (known.Contains(icon) && !cur.Remove(icon)) cur.Add(icon);
        next = cur.Count == known.Count ? null : cur;
    }
    else if (req.Preset is not ("all" or "starter" or "season1" or "season2")) return Results.BadRequest(new { error = "Unknown preset." });
    var r = table.SetActiveDecks(next);
    if (!r.Ok) return Results.BadRequest(new { error = r.Error });
    try
    {
        if (next == null) File.Delete(decksFile);
        else DeckCatalog.SaveActive(decksFile, next.OrderBy(x => x));
    }
    catch (Exception ex) { Console.WriteLine("decks: couldn't save — " + ex.Message); }
    return Results.Ok(DecksView());
});

app.MapPost("/api/host", (HostRequest req) =>
{
    var args = req.Args ?? new Dictionary<string, string>();
    var r = table.HostAction(req.Action ?? "", args);
    if (r.Ok)
    {
        string mid = table.Mode.Id;
        if (req.Action == "options") foreach (var k in new[] { "score", "timer" }) if (args.TryGetValue(k, out var v)) RememberPref(mid, k, v);
        if (req.Action == "difficulty" && args.TryGetValue("value", out var dv))
        {
            if (mid == "lanes") RememberPref(mid, "hard", dv.Equals("hard", StringComparison.OrdinalIgnoreCase) ? "1" : "0");
            else RememberPref(mid, "difficulty", dv);
        }
    }
    return r.Ok ? Results.Ok() : Results.BadRequest(new { error = r.Error });
});

app.MapPost("/api/leave", (TokenRequest req) =>
{
    var p = table.ByToken(req.Token);
    if (p == null) return Results.Unauthorized();
    table.RemovePlayer(p.Name);
    return Results.Ok();
});

// ---------- saved mixes (snapshots; docs/AUDIT.md item 17) ----------
var mixes = new MixStore(Path.Combine(root, "data"));
var padOrder = new long[5]; long padSeq = 0;                 // play order per slot, so playback re-adds cards in order
table.PadChanged += (pad, c) => { if (c != null && pad is >= 0 and < 5) padOrder[pad] = Interlocked.Increment(ref padSeq); };

object MixSummary(string id, Snapshot m, bool isDefault) => new
{
    id, name = m.Name, isDefault, source = m.Source,
    time = isDefault ? null : (DateTime?)m.Time,              // shipped defaults show no date (loaded with the epoch)
    key = MixUtil.KeyLabel(m), tempo = m.Tempo,
    slots = Enumerable.Range(0, 5).Select(i =>
    {
        var sl = m.Slots.FirstOrDefault(x => x.Slot == i);
        var c = sl == null ? null : db.ResolveCid(sl.CardId);
        return c == null ? null : new { image = c.ImageName, type = c.Type.ToString(), name = c.Name, artist = c.Artist };
    }).ToArray(),
};

app.MapGet("/api/mixes", () => mixes.All().Select(x => MixSummary(x.Id, x.S, x.Default)).ToArray());

// SnapshotButton.Select → SnapshotManager.TakeSnapshot: needs minRequiredCards cards; named by mode; duplicates aren't added
app.MapPost("/api/mixes/save", () =>
{
    if (table.Mode.Id == "mixplay") return Results.BadRequest(new { error = "This mix is already saved." });
    var cards = Enumerable.Range(0, 5).Select(i => (Slot: i, Card: table.Top(i))).Where(x => x.Card != null).ToList();
    if (GameTables.Snapshots is not { } mixRules) return Results.BadRequest(new { error = "Saved-mix settings aren't imported. Run setup again." });
    if (cards.Count < mixRules.MinRequiredCards) { director?.PlayClip("snapshot_fail"); return Results.BadRequest(new { error = Loc.Get("Snapshot_NotEnoughCards") ?? "Add more cards first" }); }
    var (tempo, key, minor) = director?.Master ?? (mix?.PlayBpm ?? 120, null, null);
    var (name, source) = table.Mode.Id switch
    {
        "freestyle" => (Loc.Get("Snapshot_StudioSnapshotName") ?? "My Freestyle", "kStudio"),
        "party" => (Loc.Get("Snapshot_PartySnapshotName") ?? "My Party", "kParty"),
        "clash" => (Loc.Get("Snapshot_H2HSnapshotName") ?? "My Clash", "kHeadToHead"),
        _ => (Loc.Get("Snapshot_LanesSnapshotName") ?? "My Puzzle", "kLanes"),
    };
    var snap = new Snapshot
    {
        Time = DateTime.Now, Name = name, Source = source, Tempo = Math.Round(tempo),
        Key = ((OpenMix.Cards.MusicKey)(key ?? 0)).ToString(), Mode = minor == true ? "Minor" : "Major",
        Slots = cards.OrderBy(x => padOrder[x.Slot]).Select(x =>
        {
            float lv = (float)(director?.Volume(x.Slot) ?? 0.683);
            return new SnapshotSlot { CardId = x.Card!.Cid, Slot = x.Slot, Level = lv, Level1 = lv, Version = 1 };
        }).ToList(),
    };
    bool dup = mixes.All().Any(e => !e.Default && e.S.Key == snap.Key && e.S.Mode == snap.Mode && e.S.Tempo == snap.Tempo
        && e.S.Slots.Count == snap.Slots.Count
        && snap.Slots.All(a => e.S.Slots.Any(b => b.Slot == a.Slot && b.CardId == a.CardId && b.Level == a.Level && b.Level1 == a.Level1)));
    string? id = dup ? null : mixes.Add(snap);
    Console.WriteLine(dup ? "mixes: already saved" : $"mixes: saved {name} ({snap.Slots.Count} cards)");
    if (director?.StudioOpen != true) director?.PlayClip("snapshot_success");   // not while the Studio controls are open
    walkthrough.MixSaved();
    return Results.Ok(new { ok = true, id, duplicate = dup, message = Loc.Get("Snapshot_Captured") ?? "Saved" });
});

// Menu_SnapshotViewer.PlaySnapshot: set key/tempo, then the cards enter in saved order every 8 beats
app.MapPost("/api/mixes/play", (MixRequest req) =>
{
    var e = mixes.All().FirstOrDefault(x => x.Id == req.Id);
    if (e.S == null) return Results.NotFound(new { error = "That mix is gone." });
    var m = e.S;
    var entries = m.Slots.Select(sl => (sl.Slot, Card: db.ResolveCid(sl.CardId))).Where(x => x.Card != null).Select(x => (x.Slot, x.Card!)).ToList();
    if (entries.Count == 0) return Results.BadRequest(new { error = "None of this mix's cards are known." });
    if (GameTables.Snapshots == null) return Results.BadRequest(new { error = "Saved-mix settings aren't imported. Run setup again." });
    if (director != null)
    {
        mixVolumesBefore ??= Enumerable.Range(0, 5).Select(director.Volume).ToArray();
        director.PinMaster = true;
        if (Enum.TryParse<OpenMix.Cards.MusicKey>(m.Key, true, out var k) && k != OpenMix.Cards.MusicKey.None) director.SetKey((int)k, m.Mode == "Minor");
        director.SetTempo(m.Tempo);
        for (int i = 0; i < 5; i++)
        {
            var sl = m.Slots.FirstOrDefault(x => x.Slot == i);
            director.SetSlotVolume(i, sl == null ? 0.683 : MixUtil.SnapshotVolume(sl));
        }
    }
    table.SetMode(new MixPlaybackMode(e.Id, m.Name, e.Default, entries, m.Tempo, MixUtil.KeyLabel(m)));
    return Results.Ok(new { ok = true });
});

// export (viewer's export button, local): renders the mix to .wav + saves the mix .xml in Documents/OpenMix Exports
var exporting = 0;
app.MapPost("/api/mixes/export", async (MixRequest req) =>
{
    var e = mixes.All().FirstOrDefault(x => x.Id == req.Id);
    if (e.S == null) return Results.NotFound(new { error = "That mix is gone." });
    if (Interlocked.Exchange(ref exporting, 1) == 1) return Results.BadRequest(new { error = "Already exporting a mix." });
    try
    {
        var (wav, xml) = await Task.Run(() => MixExport.Export(e.S, db, audioDir));
        // SFXCard "share": a mix featuring N cards from a playlist (snapshot_shareMix)
        foreach (var g in e.S.Slots.Select(sl => db.ResolveCid(sl.CardId)).Where(c => c != null).GroupBy(c => c!.Icon))
            badges.Track("snapshot_shareMix", null, g.Key, g.Count());
        Console.WriteLine($"export: {wav}");
        if (!opt.NoWindow || req.Name != "noreveal") MixExport.Reveal(wav);
        return Results.Ok(new { ok = true, wav, xml, folder = Path.GetDirectoryName(wav) });
    }
    catch (Exception ex) { Console.WriteLine("export: " + ex); return Results.BadRequest(new { error = "Export failed: " + ex.Message }); }
    finally { Volatile.Write(ref exporting, 0); }
});

// Menu_DeckVerification (a checker: Menu_DeckBuilder counts the five stacks as one deck; nothing is saved)
app.MapPost("/api/deckbuilder", (DeckBuilderRequest req) => { deckBuilderOpen = req.Open; return Results.Ok(new { open = deckBuilderOpen }); });
app.MapGet("/api/deckbuilder", () =>
{
    List<(int Slot, Card Card)> on; lock (boardOn) on = boardOn.ToList();
    return new
    {
        open = deckBuilderOpen, board = boardStatus.State,
        cards = on.Select(x => new { slot = x.Slot, cid = x.Card.Cid, name = x.Card.Name, type = x.Card.Type.ToString(), power = x.Card.Power, image = x.Card.ImageName, known = !x.Card.Cid.StartsWith("unknown_") }).ToArray(),
    };
});

// ---- Add to Collection: the board's card UIDs are read (A4) and, on ADD, each new
// card's signature (A6) is checked against the app's public key (secp128r1) before it joins data/collection.json.
// With --board-sim, the sim's cards are "signed" with a throwaway test key (UIDs ending in BD carry a bad signature).
var simKey = System.Numerics.BigInteger.Parse("0C0FFEE0DD0123456789ABCDEF", System.Globalization.NumberStyles.HexNumber);
var simPub = CardSignature.PublicFor(simKey);
async Task<List<(int Slot, ulong Uid)>> ReadBoardUids()
{
    var list = new List<(int, ulong)>();
    if (opt.BoardSim) { lock (simUids) list.AddRange(simUids.Select(x => (x.Slot, x.Uid))); return list; }
    if (liveBoard is not { } b) return list;
    var stacks = b.Contents();
    for (int i = 0; i < 5; i++)
        if (i < stacks.Length && stacks[i].Count > 0)
            foreach (var u in await b.ReadUidsAsync((Slot)i)) list.Add((i, u));
    return list;
}
// The card's signature from the board if it verifies, else null. The collection keeps it as proof of ownership.
async Task<byte[]?> CheckCard(ulong uid)
{
    if (opt.BoardSim)
    {   // development only: simulated cards are signed with a test key, so they never count as owned for downloads
        await Task.Delay(60);
        var s = CardSignature.SignForTest(uid, simKey, 0x1234567 + (long)(uid & 0xFFFF));
        return (uid & 0xFF) != 0xBD && CardSignature.VerifyCard(uid, s, simPub) ? s : null;
    }
    if (liveBoard is not { } b) return null;
    var sig = await b.ReadSignatureAsync(uid & 0xFFFF_FFFF_FFFFUL);
    bool ok = CardSignature.VerifyCard(uid, sig);
    if (!ok) Console.WriteLine($"collection: card {uid:X16} signature {(sig.Length == 0 ? "not returned" : Convert.ToHexString(sig) + " did not verify")}");
    return ok ? sig : null;
}
_ = Task.Run(async () =>
{   // while the screen is open: re-read the UIDs whenever the board's contents change (and every few seconds)
    DateTime last = DateTime.MinValue;
    while (true)
    {
        await Task.Delay(250);
        if (!claimOpen || claim.Busy) { last = DateTime.MinValue; continue; }
        if (!claim.Dirty && DateTime.UtcNow - last < TimeSpan.FromSeconds(4)) continue;
        claim.Dirty = false; last = DateTime.UtcNow;
        try { claim.Set(await ReadBoardUids(), collection); table.Touch(); }
        catch (Exception ex) { Console.WriteLine($"collection: UID read failed ({ex.Message})"); claim.Dirty = true; }
    }
});
app.MapPost("/api/collection/claim", (DeckBuilderRequest req) =>
{
    claimOpen = req.Open; claim.Dirty = true;
    if (req.Open) claim.Message = null;
    return Results.Ok(new { open = claimOpen });
});
app.MapGet("/api/collection/claim", () => claim.View(claimOpen, opt.BoardSim ? "connected" : boardStatus.State, collection, db));
app.MapPost("/api/collection/claim/add", async () =>
{
    if (!claimOpen) return Results.BadRequest(new { error = "The Add to Collection screen isn't open." });
    if (!claim.TryBegin()) return Results.Ok(claim.View(claimOpen, boardStatus.State, collection, db));
    try
    {
        var uids = await ReadBoardUids();
        claim.Set(uids, collection);
        var fresh = uids.Where(x => !collection.Owns(x.Uid)).ToList();
        if (uids.Count > ClaimState.MaxTotal || uids.GroupBy(x => x.Slot).Any(g => g.Count() > ClaimState.MaxPerSlot))
        { claim.Message = string.Format(Loc.Get("Collection_ClaimTooManyCards") ?? "Too many cards: the board can check at most {0} at a time.", ClaimState.MaxTotal); claim.Error = true; }
        else if (fresh.Count == 0)
        { claim.Message = string.Format(Loc.Get("Collection_ClaimCardsResult") ?? "Added {0} cards.", 0); claim.Error = false; }
        else
        {
            var before = collection.OwnedCids();
            int added = 0, failed = 0; var icons = new HashSet<string>();
            foreach (var (slot, uid) in fresh)
            {
                var card = db.Resolve(BoardProtocol.CtiFromUid(uid));
                if (card == null) { failed++; continue; }                     // not a card this app knows
                byte[]? sig; try { sig = await CheckCard(uid); } catch (Exception ex) { Console.WriteLine($"collection: signature read failed ({ex.Message})"); sig = null; }
                if (sig == null) { failed++; continue; }
                if (collection.Add(uid, card.Cid, card.Icon, sig)) { added++; if (card.Icon.Length > 0) icons.Add(card.Icon); }
            }
            int newCards = collection.OwnedCids().Except(before).Count();
            Console.WriteLine($"collection: {added} added, {failed} failed, {newCards} new cards (collection {collection.Count})");
            if (added > 0) RefreshAvailable();                              // newly scanned cards join the virtual deck
            var owned = collection.OwnedCids();
            foreach (var icon in icons)                                    // Badge_RegisterCards: every card of the playlist registered
                if (table.AllCards.Where(c => c.Icon == icon).All(c => owned.Contains(c.Cid))) badges.Track("collection_registerAll", null, icon);
            claim.Error = added == 0;
            claim.Message = added == 0 ? Loc.Get("Collection_ClaimCardsFailed") ?? "No cards were added."
                : string.Format(Loc.Get("Collection_ClaimCardsResult") ?? "Added {0} cards.", added)
                  + (failed > 0 ? $" {failed} could not be verified." : "");
            claim.NewCards = newCards;
            claim.Set(await ReadBoardUids(), collection);
        }
        claim.ResultAt = DateTime.UtcNow;
    }
    finally { claim.End(); table.Touch(); }
    return Results.Ok(claim.View(claimOpen, boardStatus.State, collection, db));
});
// Card content (Settings → Menu_Settings_CardCache): opt-in, as in the original app
app.MapGet("/api/cardcontent", () => cardContent.Details());
// (HostGuard: only the OpenMix window on this PC can call these)
app.MapPost("/api/cardcontent/download", () => { cardContent.Prepare(); return Results.Ok(); });   // asks the size first
app.MapPost("/api/cardcontent/confirm", () => { cardContent.Confirm(); return Results.Ok(); });
app.MapPost("/api/cardcontent/cancel", () => { cardContent.Cancel(); return Results.Ok(); });
app.MapPost("/api/cardcontent/delete", () => { cardContent.DeleteAll(); return Results.Ok(); });
app.MapPost("/api/collection/reset", () => { collection.Reset(); claim.Dirty = true; RefreshAvailable(); return Results.Ok(); });

// table UI sounds (SFXCard/button_select, slider, card_removed, Actions/SplashAnim): bank clips, random variant
app.MapPost("/api/uisfx", (MixRequest req) =>
{
    if (req.Name is "button_select" or "slider_" or "SplashAnim" or "card_removed_scratch" or "card_removed_fall" or "card_inspect") director?.UiSfx(req.Name);
    return Results.Ok();
});

app.MapPost("/api/mixes/rename", (MixRequest req) =>
{
    var name = (req.Name ?? "").Trim();
    if (name.Length == 0 || name.Length > 40) return Results.BadRequest(new { error = "Names are 1–40 characters." });
    if (!mixes.Rename(req.Id ?? "", name)) return Results.BadRequest(new { error = "Only your own mixes can be renamed." });
    if (table.Mode is MixPlaybackMode pm && pm.MixId == req.Id) { pm.Name = name; table.Touch(); }
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/mixes/delete", (MixRequest req) =>
{
    if (!mixes.Delete(req.Id ?? "")) return Results.BadRequest(new { error = "Only your own mixes can be deleted." });
    if (table.Mode is MixPlaybackMode pm && pm.MixId == req.Id) table.SetMode("freestyle");
    return Results.Ok(new { ok = true });
});

// Live state: Server-Sent Events. Holding this stream open is what "connected" means.
app.MapGet("/api/stream", async (HttpContext ctx, string token) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    var ct = ctx.RequestAborted;

    if (!table.Connect(token))
    {
        await ctx.Response.WriteAsync("event: kicked\ndata: {}\n\n", ct);
        return;
    }
    try
    {
        long sent = -1; var lastBeat = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            if (table.ByToken(token) == null)          // name was reclaimed elsewhere
            {
                await ctx.Response.WriteAsync("event: kicked\ndata: {}\n\n", ct);
                break;
            }
            if (table.Version != sent)
            {
                var snap = table.Snap(token);
                sent = snap.Version;
                await ctx.Response.WriteAsync($"event: state\ndata: {JsonSerializer.Serialize(Views.Of(snap), json)}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                lastBeat = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - lastBeat > TimeSpan.FromSeconds(10))
            {
                await ctx.Response.WriteAsync(": ping\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                lastBeat = DateTime.UtcNow;
            }
            await Task.Delay(100, ct);
        }
    }
    catch (OperationCanceledException) { }
    finally { table.Disconnect(token); }
});

// Board light timing: GET shows the extra offset, POST {"offset": seconds} sets it (+ = lights earlier).
// BoardConnectPanel.RetryConnection: out of range → ignore the RSSI threshold for the next scan
app.MapPost("/api/board/retry", () => { ignoreRssiNext = true; boardStatus.State = "searching"; table.Touch(); return Results.Ok(); });

app.MapGet("/api/board/lights", () => new { offset = BoardLights.ExtraOffset });
// testing without the physical board (--board-sim): feed place/lift events as the BLE board would.
// cti not in the database = an unknown card (the same placeholder the board loop uses).
if (opt.BoardSim)
    app.MapPost("/api/board/sim", (BoardSimRequest req) =>
    {
        Card? card = req.Cid != null ? db.ResolveCid(req.Cid) : null;
        if (card == null && req.Cti is int cti)
            lock (unknownCards) if (!unknownCards.TryGetValue(cti, out card))
                unknownCards[cti] = card = new Card { Cid = $"unknown_{cti}", Cti = (ushort)cti, Name = "Unknown card" };
        if (card == null || req.Slot is not (>= 0 and < 5)) return Results.BadRequest(new { error = "Need slot 0-4 and a cid or cti." });
        if (!deckBuilderOpen && !claimOpen) table.BoardCard(req.Slot, card, req.Added);
        lock (boardOn)
        {
            if (req.Added) boardOn.Add((req.Slot, card));
            else { int i = boardOn.FindLastIndex(x => x.Slot == req.Slot && ReferenceEquals(x.Card, card)); if (i >= 0) boardOn.RemoveAt(i); }
        }
        lock (simUids)
        {   // the sim's physical UIDs: CTI in the top 16 bits (as on real cards), else the request's or a running serial
            if (req.Added) simUids.Add((req.Slot, card, req.Uid != null ? Convert.ToUInt64(req.Uid, 16) : ((ulong)card.Cti << 48) | 0x5100_0000_0000UL | (ulong)++simSerial));
            else { int i = simUids.FindLastIndex(x => x.Slot == req.Slot && ReferenceEquals(x.Card, card)); if (i >= 0) simUids.RemoveAt(i); }
        }
        claim.Dirty = true;
        UnknownChanged(req.Slot, card, req.Added);
        return Results.Ok(new { ok = true, card = card.Name });
    });

app.MapPost("/api/board/lights", (LightsRequest req) => { if (req.Offset is double o) BoardLights.ExtraOffset = Math.Clamp(o, -2, 2); return new { offset = BoardLights.ExtraOffset }; });

// Host info for the big-screen view (join URL / QR).
app.MapGet("/api/info", () => new
{
    joinUrls = LanAddresses().Select(ip => $"http://{ip}:{opt.Port}/").ToArray(),
    port = opt.Port,
    board = opt.Board,
    boardStatus = boardStatus,
});

// Spectator stream for the big-screen table view: full table state, no hand, not a player.
// Slot VU levels + heard beat for the table's meters and timeline (~30 Hz; the screen animates between samples).
app.MapGet("/api/levels", async (HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var ct = ctx.RequestAborted;
    try
    {
        while (!ct.IsCancellationRequested)
        {
            if (vu != null) await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(vu.Now())}\n\n", ct);
            else await ctx.Response.WriteAsync(": no audio\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
            await Task.Delay(vu != null ? 33 : 5000, ct);
        }
    }
    catch (OperationCanceledException) { }
});

app.MapGet("/api/watch", async (HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var ct = ctx.RequestAborted;
    try
    {
        long sent = -1; var lastBeat = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            if (table.Version != sent)
            {
                var snap = table.Snap();
                sent = snap.Version;
                await ctx.Response.WriteAsync($"event: state\ndata: {JsonSerializer.Serialize(Views.Of(snap), json)}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                lastBeat = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - lastBeat > TimeSpan.FromSeconds(10))
            {
                await ctx.Response.WriteAsync(": ping\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                lastBeat = DateTime.UtcNow;
            }
            await Task.Delay(100, ct);
        }
    }
    catch (OperationCanceledException) { }
});

// card art
app.MapGet("/art/{image}", (HttpContext ctx, string image) =>
{
    var safe = Path.GetFileNameWithoutExtension(image);
    var jpg = Path.Combine(thumbDir, safe + ".jpg");
    if (File.Exists(jpg)) { ctx.Response.Headers.CacheControl = "public, max-age=86400"; return Results.File(jpg, "image/jpeg"); }
    var png = Path.Combine(pngDir, safe + ".png");
    if (File.Exists(png)) { ctx.Response.Headers.CacheControl = "public, max-age=86400"; return Results.File(png, "image/png"); }
    ctx.Response.Headers.CacheControl = "no-store";   // never cache a miss: art not downloaded yet must re-fetch once it is
    return Results.NotFound();
});
// Original game UI art (slot frames, logo, icons, crowd), served from the ripped assets.
string uiDir = Path.Combine(root, "assets_raw");
app.MapGet("/ui/{name}", (HttpContext ctx, string name) =>
{
    var safe = Path.GetFileNameWithoutExtension(name);
    // APK sprites first, then the OBB's (assets_raw/obb_ui; a few exported names carry a trailing space)
    var path = new[] { Path.Combine(uiDir, safe + ".png"), Path.Combine(uiDir, "obb_ui", safe + ".png"), Path.Combine(uiDir, "obb_ui", safe + " .png"), Path.Combine(uiDir, "tutorial", safe + ".png") }
        .FirstOrDefault(File.Exists);
    if (path == null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.File(path, "image/png");
});
// The game's own fonts, when present in the content folder (system fonts otherwise)
app.MapGet("/font/{name}", (HttpContext ctx, string name) =>
{
    var safe = Path.GetFileName(name);
    var path = Path.Combine(uiDir, "fonts", safe);
    if (!safe.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.File(path, "font/ttf");
});
if (!Directory.Exists(thumbDir))
    Console.WriteLine("art: no card images yet — add your cards on the board to fetch them.");

Console.WriteLine();
Console.WriteLine("Phones on the same Wi-Fi can join at:");
foreach (var ip in LanAddresses()) Console.WriteLine($"   http://{ip}:{opt.Port}/");
Console.WriteLine($"   (this PC: http://localhost:{opt.Port}/)");
if (opt.NoWindow) Console.WriteLine($"Table (host controls): {HostGuard.WindowUrl($"http://localhost:{opt.Port}/table.html")}");
Console.WriteLine("If phones can't reach it, allow OpenMix through Windows Firewall (Private networks).");
Console.WriteLine();

app.Lifetime.ApplicationStopping.Register(() => { boardCts.Cancel(); player?.Dispose(); });
if (opt.NoWindow) app.Run();
else
{
    // Standalone app: the table lives in DropMix's own native window (Photino: WebView2 on Windows, WebKitGTK on
    // Linux). The server for the phones runs inside this process; closing the window stops everything.
    await app.StartAsync();
    RunTableWindow(HostGuard.WindowUrl($"http://localhost:{opt.Port}/table.html"), app.Lifetime);
    Console.WriteLine("window: closed — shutting down");
    // stop cleanly (board link, audio) but don't wait on open phone/table streams: the window closing means off
    using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        try { await app.StopAsync(stopCts.Token); } catch { }
    Console.WriteLine("stopped");
    Environment.Exit(0);
}

// ---------------- helpers ----------------

object DecksView()
{
    var active = decks.Decks.Where(d => table.IsDeckActive(d.Icon)).ToList();
    // every card counts as owned, so each deck is fully owned and the collection total equals the catalog
    return new
    {
        decks = decks.Decks.Select(d => new { d.Icon, d.Name, d.Kind, d.Season, d.Count, active = table.IsDeckActive(d.Icon), owned = d.Count }),
        ownedCount = table.AllCards.Count,
        totalCards = table.AllCards.Count,
        activeCount = active.Count,
        cardCount = table.ActiveCards.Count,
        all = table.ActiveDecks == null,
    };
}

// Content root: the imported copy in %LOCALAPPDATA%\OpenMix\content, else (development) a folder above the
// app that holds data/cards.csv. Null = nothing imported yet.
static string? TryFindContentRoot()
{
    var imported = ContentPaths.Default;
    if (File.Exists(Path.Combine(imported, "data", "cards.csv"))) return imported;
    foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "data", "cards.csv"))) return d.FullName;
    }
    return null;
}

// The big-screen game view: Edge in app mode (chromeless window), else the default browser.
/// <summary>Opens the table in a native window and blocks until the user closes it (Photino needs an STA thread with
/// its own message loop). If the app is stopped another way, the window is closed too.</summary>
static void RunTableWindow(string url, IHostApplicationLifetime life)
{
    var t = new Thread(() =>
    {
        try
        {
            var w = new Photino.NET.PhotinoWindow()
                .SetTitle("OpenMix")
                .SetUseOsDefaultSize(false).SetSize(1600, 900).Center()
                .SetResizable(true)
                .SetMaximized(true)
                .SetContextMenuEnabled(false)
                .SetDevToolsEnabled(false)
                .SetLogVerbosity(0);
            // the web UI asks for fullscreen (F11 / the Settings toggle) through a web message, since only the native
            // window can cover the whole monitor
            w.RegisterWebMessageReceivedHandler((object? sender, string msg) =>
            {
                if (sender is Photino.NET.PhotinoWindow win && msg == "fullscreen")
                    try { win.SetFullScreen(!win.FullScreen); } catch { }
            });
            var ico = Path.Combine(AppContext.BaseDirectory, "app.ico");   // OpenMix icon
            if (File.Exists(ico)) w.SetIconFile(ico);
            w.Load(new Uri(url));
            // if the app stops some other way, close the window — but drop the hook once the window is gone: Close()
            // on a finished window waits forever for its (ended) UI thread
            var reg = life.ApplicationStopping.Register(() => { try { w.Close(); } catch { } });
            w.WaitForClose();
            reg.Dispose();
        }
        catch (Exception ex) { Console.WriteLine($"window: couldn't open ({ex.Message}) — browse to {url}"); Thread.Sleep(Timeout.Infinite); }
    });
    if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);
    t.Start(); t.Join();
}

static IEnumerable<string> LanAddresses() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
        .Select(a => a.Address.ToString())
        .Distinct();

record JoinRequest(string? Name, string? Token);
record KickRequest(string? Name);
record PlayRequest(string? Token, string? Cid, int Pad);
record TokenRequest(string? Token);
record PadRequest(string? Token, int Pad);
record TeamRequest(string? Token, string? Team);
record ModeActionRequest(string? Token, string? Action, string? Arg);
record ModeRequest(string? Mode, Dictionary<string, string>? Options);
record HostRequest(string? Action, Dictionary<string, string>? Args);
record DecksRequest(string[]? Active, string[]? Toggle, string? Preset);
record SettingsRequest(double? Music, double? Effects, double? Crowd, bool? Reminders, string? Language, string? ArtDownload);
record PauseRequest(bool On);
record LightsRequest(double? Offset);
record StudioRequest(string? Action, double? Value, int? Slot);
record MixRequest(string? Id, string? Name);
record CardDisableRequest(string? Cid, bool Enabled);
record BoardSimRequest(int Slot, string? Cid, int? Cti, bool Added, string? Uid = null);
record DeckBuilderRequest(bool Open);

sealed class BoardStatus
{
    public bool Enabled { get; set; }
    public string State { get; set; } = "off";          // off | scanning | searching | connected | lost
    public string? Name { get; set; }
    public string? Firmware { get; set; }
    public int Battery { get; set; } = -1;
    public double BatteryLevel { get; set; } = -1;               // 0..1 display bar (the original's battery monitor)
    public bool BatteryLow { get; set; }
    public bool BatteryCritical { get; set; }
    public bool ForcedOff { get; set; }                          // disconnected for a low battery: show the popup
    public int[] UnknownSlots { get; set; } = Array.Empty<int>();  // slots holding a card the database doesn't know (> 2 s)
}

sealed class Options
{
    public int Port = 5080; public bool Board; public bool NoBoard; public bool BoardTrace; public bool NoAudio; public bool NoWindow; public bool BoardSim; public bool NoGate; public int Hand = 5;
    public double GraceSeconds = 15; public string? Root; public string Mode = "freestyle";
    public static Options Parse(string[] a)
    {
        var o = new Options();
#if DEBUGTOOLS
        // openmix-debug.exe (built with -p:DebugTools=true): open straight to the game UI with a simulated board,
        // so desktop and phone screenshots can be taken without a DropMix board or owned cards. Flags still override.
        o.NoGate = true; o.BoardSim = true; o.NoBoard = true;
#endif
        for (int i = 0; i < a.Length; i++)
            switch (a[i])
            {
                case "--port": o.Port = int.Parse(a[++i]); break;
                case "--board": o.Board = true; break;
                case "--no-board": o.NoBoard = true; break;
                case "--board-trace": o.Board = true; o.BoardTrace = true; break;
                case "--no-audio": o.NoAudio = true; break;
                case "--no-window": o.NoWindow = true; break;
#if DEBUG || DEBUGTOOLS
                // development only; release builds have no way around the board check
                case "--board-sim": o.BoardSim = true; break;
                case "--no-gate": o.NoGate = true; break;
                case "--gate": o.NoGate = false; break;      // re-enable the ownership gate in a debug build
#endif
                case "--hand": o.Hand = int.Parse(a[++i]); break;
                case "--grace": o.GraceSeconds = double.Parse(a[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--root": case "--content": o.Root = a[++i]; break;
                case "--mode": o.Mode = a[++i]; break;
            }
        if (!o.NoWindow && !o.NoBoard) o.Board = true;      // the standalone app uses the board unless told not to
        return o;
    }
}

// Compact JSON views for the phone (keeps the payload small and stable).
static class Views
{
    public record CardV(string Cid, string Name, string Artist, string Type, string Image, string Key, string Mode, float Bpm, int Power, string? Fx,
        string[] Instruments, string Icon, string? Num, string? Print, string? Ability, string? Picto, string[]? Genres = null, string? Year = null, string? Illustrator = null, string? Credits = null);
    /// <summary>Printed details (print ID, "NN/15") from the set CSVs, for the physical-card look on phones.</summary>
    public static Dictionary<string, (string print, int index, int count)> Print = new();
    public record PadV(int Pad, CardV? Top, string? Source, string? PlayedBy, int Stack, string? Owner, bool Silenced, bool Valid);
    public record PlayerV(string Name, string Status, int HandCount, string Team, bool Board);
    public record StateV(long Version, string? You, PadV[] Pads, CardV[] Hand, PlayerV[] Players, int Deck, object Mode, object? Mix, object? Board, object? Badges, object? Walk, object? Gate);
    /// <summary>Mix tempo/key/bar for the screens (set by the host when audio is on).</summary>
    public static Func<object?> MixState = () => null;
    public static Func<object?> BoardState = () => null;
    public static Func<object?> BadgeState = () => null;
    public static Func<object?> WalkState = () => null;
    public static Func<object?> GateState = () => null;
    public static Func<string> ModeId = () => "";

    public static CardV Of(Card c) => new(c.Cid, c.Name, c.Artist, c.Type.ToString(), c.ImageName,
        c.Key.ToString(), c.Mode.ToString(), c.Tempo, c.Power,
        c.Type != CardType.Fx ? null
        : ModeId() == "lanes" ? "Clears every single request, plus Levels in its row"   // Lanes ignores the card's own ability
        : ModeId() == "party" ? "Meets any request on screen (for no points)"           // Party: FX dismisses every active request
        : (c.ScreenText.Length > 0 ? Loc.Get(c.ScreenText) : null) ?? FxAbility.Parse(c.Ability)?.Describe(),
        c.Instruments, c.Icon,
        Print.TryGetValue(c.Cid, out var pi) && pi.count > 0 ? $"{pi.index:00}/{pi.count}" : null,
        Print.TryGetValue(c.Cid, out var pj) && pj.print.Length > 0 ? pj.print : null,
        c.Type == CardType.Fx && c.Ability.Length > 0 ? c.Ability : null,
        c.Type == CardType.Fx && c.ScreenText.StartsWith("fx_") ? "fxp_" + c.ScreenText[3..] : null,   // official pictogram (assets_raw/obb_ui)
        c.Genres.Length > 0 ? c.Genres.Select(g => g.StartsWith("genre_") ? g[6..] : g).ToArray() : null,
        c.Year.Length > 0 ? c.Year : null, c.Illustrator.Length > 0 ? c.Illustrator : null, c.Credits.Length > 0 ? c.Credits : null);

    public static StateV Of(Table.Snapshot s) => new(
        s.Version, s.You,
        s.Pads.Select(p => new PadV(p.Pad, p.Top is null ? null : Of(p.Top.Card), p.Top?.Source.ToString(), p.Top?.PlayedBy, p.StackCount,
            p.Top?.Owner is Side o && o != Side.None ? o.ToString() : null, p.Top?.Silenced ?? false, p.Top?.Valid ?? true)).ToArray(),
        (s.Hand ?? Array.Empty<Card>()).Select(Of).ToArray(),
        s.Players.Select(p => new PlayerV(p.Name, p.Status.ToString(), p.HandCount, p.Team.ToString(), p.Board)).ToArray(),
        s.DeckCount, s.Mode, MixState(), BoardState(), BadgeState(), WalkState(), GateState());
}
