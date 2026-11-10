using OpenMix.Cards;

namespace OpenMix.Game;

/// <summary>
/// Mix playback (Menu_SnapshotViewer): a saved mix plays back on a virtual board. The host sets the saved
/// key/tempo/levels first; the cards then enter in the order they were played, one every
/// <c>beatsBetweenCards</c> beats (imported). Physical cards and phones are ignored while it plays.
/// </summary>
public sealed class MixPlaybackMode : GameMode
{
    /// <summary>Menu_SnapshotViewer.beatsBetweenCards (imported); throws without the imported tables.</summary>
    public static int BeatsBetweenCards => GameTables.Snapshots?.BeatsBetweenCards
        ?? throw new InvalidOperationException("saved-mix settings aren't imported; run setup again");

    public override string Id => "mixplay";
    public override string Title => "Mix Playback";
    public override bool AcceptsBoardCards => false;

    public string MixId { get; }
    public string Name { get; set; }
    public bool IsDefault { get; }
    readonly List<(int Pad, Card Card)> _entries;
    readonly double _tempo; readonly string _keyLabel;
    readonly TimeSpan _interval;
    DateTime _next; int _entered;
    string? _credit;

    public MixPlaybackMode(string mixId, string name, bool isDefault, IEnumerable<(int Pad, Card Card)> entries, double tempo, string keyLabel)
    {
        MixId = mixId; Name = name; IsDefault = isDefault;
        _entries = entries.Where(e => e.Pad >= 0 && e.Pad < Rules.Slots).ToList();
        _tempo = tempo; _keyLabel = keyLabel;
        _interval = TimeSpan.FromSeconds(BeatsBetweenCards * 60.0 / Math.Max(40, tempo));
    }

    public override void Start() { _entered = 0; _next = DateTime.UtcNow.AddMilliseconds(300); }

    public override bool Tick(DateTime now)
    {
        if (_entered >= _entries.Count || now < _next) return false;
        var (pad, card) = _entries[_entered++];
        T.Pads[pad].Clear();
        T.Pads[pad].Add(new PadCard { Card = card, Source = CardSource.Board });
        _credit = $"{card.Artist} — {card.Name}";
        _next = now + _interval;
        return true;
    }

    public override void Resumed(TimeSpan d) => _next += d;
    public override void BoardLifted(Card card, int pad) { }       // the board isn't in play during playback

    public override ActionResult Play(Player p, Card card, int pad) => ActionResult.Fail("A saved mix is playing.");

    public override object View(Player? viewer) => new
    {
        id = Id, title = Title, subtitle = Name,
        phase = "play",
        slots = SlotViews(),
        canPlay = false, allowDraw = false, allowClear = false, allowUndo = false,
        mix = new
        {
            id = MixId, name = Name, isDefault = IsDefault,
            entered = _entered, total = _entries.Count,
            // credits: each card as it enters, then "<tempo> • <key>" once all are in
            credit = _entered >= _entries.Count ? $"{_tempo:0} BPM • {_keyLabel}" : _credit,
        },
    };
}
