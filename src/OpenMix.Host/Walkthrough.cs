using System.Text.Json;
using OpenMix.Cards;

/// <summary>
/// The interactive introduction (Walkthrough, FTUE Bank.bank): three stages (Intro, Placement, Outro) of voiced steps,
/// imported into data/walkthrough.json. It runs on top of Freestyle. Each step waits its start
/// delay (cut short by a card if playEarlyIfCardPlayed), then plays its VO line, shows its text and highlights, and
/// marks its slot. While it waits to finish, the timeout line repeats every timeoutDelay, and the wrong-slot line plays
/// when the requested card type lands in another slot. Skip predicates are checked as the step begins.
/// </summary>
public sealed class WalkthroughRunner
{
    sealed record Step(string? Vo, string? Text, bool UsePreviousText, bool PlayEarly, string? VoWrongSlot, int Slot,
        bool UseSlotEffect, double StartDelay, CardType Required, double Timeout, string? VoTimeout, string SkipWhen,
        string FinishWhen, string? Popup, string[] Enable, string[] Disable);
    sealed record Stage(string Name, bool ShowSkip, bool UseSlotType, Step[] Steps);

    readonly Stage[] _stages;
    readonly CardType[] _slotTypes;
    readonly double _lastFull, _lastFade;
    readonly object _lock = new();

    // the host's hooks
    readonly Func<int, (CardType? Top, int Count)> _slot;
    readonly Func<string, double> _playVo;          // VO event -> plays it (cutting off the last line), returns seconds
    readonly Action _stopVo;
    public event Action? Finished;

    // run state
    public bool Active { get; private set; }
    int _stage, _step;
    bool _started, _popupClosed, _mixSaved;
    DateTime _startAt, _voEnds, _nextTimeout, _lastCardAt = DateTime.MinValue, _stepEnteredAt;
    string? _text, _popup, _lastCard;
    DateTime _lastCardShown;
    int _slotFx = -1;
    readonly HashSet<string> _on = new();
    long _serial;

    public WalkthroughRunner(string dataDir, Func<int, (CardType? Top, int Count)> slot, Func<string, double> playVo, Action stopVo)
    {
        _slot = slot; _playVo = playVo; _stopVo = stopVo;
        var f = Path.Combine(dataDir, "walkthrough.json");
        _stages = Array.Empty<Stage>(); _slotTypes = Array.Empty<CardType>();
        if (!File.Exists(f)) { Console.WriteLine("walkthrough: data/walkthrough.json missing"); return; }
        using var d = JsonDocument.Parse(File.ReadAllText(f));
        var r = d.RootElement;
        _lastFull = r.GetProperty("lastPlayedTextFullTime").GetDouble();
        _lastFade = r.GetProperty("lastPlayedTextFadeTime").GetDouble();
        _slotTypes = r.GetProperty("slotTypes").EnumerateArray().Select(x => Enum.Parse<CardType>(x.GetString()!)).ToArray();
        static string? S(JsonElement e, string n) => e.GetProperty(n).ValueKind == JsonValueKind.String ? e.GetProperty(n).GetString() : null;
        _stages = r.GetProperty("stages").EnumerateArray().Select(st => new Stage(
            st.GetProperty("name").GetString()!, st.GetProperty("showSkip").GetBoolean(), st.GetProperty("useSlotType").GetBoolean(),
            st.GetProperty("steps").EnumerateArray().Select(s => new Step(
                S(s, "vo"), S(s, "text"), s.GetProperty("usePreviousText").GetBoolean(), s.GetProperty("playEarlyIfCardPlayed").GetBoolean(),
                S(s, "voWrongSlot"), s.GetProperty("slot").GetInt32(), s.GetProperty("useSlotEffect").GetBoolean(),
                s.GetProperty("startDelay").GetDouble(), Enum.Parse<CardType>(s.GetProperty("requiredType").GetString()!),
                s.GetProperty("timeout").GetDouble(), S(s, "voTimeout"), s.GetProperty("skipWhen").GetString()!,
                s.GetProperty("finishWhen").GetString()!, S(s, "customPopup"),
                s.GetProperty("enable").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                s.GetProperty("disable").EnumerateArray().Select(x => x.GetString()!).ToArray())).ToArray())).ToArray();
    }

    Step Cur => _stages[_stage].Steps[_step];

    public void Start()
    {
        lock (_lock)
        {
            if (_stages.Length == 0) return;
            _stopVo();
            Active = true; _stage = 0; _step = -1; _text = null; _popup = null; _on.Clear(); _slotFx = -1; _lastCard = null;
            Next();
            _serial++;
        }
    }

    public void Skip() { lock (_lock) if (Active) Finish(); }
    public void PopupClosed() { lock (_lock) _popupClosed = true; }
    public void MixSaved() { lock (_lock) _mixSaved = true; }

    void Finish()
    {
        _stopVo();
        Active = false; _text = null; _popup = null; _on.Clear(); _slotFx = -1; _serial++;
        Console.WriteLine("walkthrough: finished");
        Finished?.Invoke();
    }

    /// <summary>A card went onto the table (board or phone): the "last played" line, early starts, wrong-slot VO.</summary>
    public void CardPlaced(int slot, Card card)
    {
        lock (_lock)
        {
            if (!Active) return;
            _lastCardAt = DateTime.UtcNow; _lastCard = card.Name; _lastCardShown = DateTime.UtcNow; _serial++;
            if (_step < 0) return;
            var s = Cur;
            if (_started && s.VoWrongSlot != null && slot != s.Slot && card.Type == s.Required && DateTime.UtcNow >= _voEnds)
                PlayVo(s.VoWrongSlot);
        }
    }

    void PlayVo(string ev)
    {
        double len = _playVo(ev);
        _voEnds = DateTime.UtcNow.AddSeconds(len);
        Console.WriteLine($"walkthrough: VO {ev} ({len:0.0} s)");
    }

    bool HasCardInSlot(Step s)
    {
        if (s.Slot is < 0 or > 4) return false;
        var (top, _) = _slot(s.Slot);
        if (top == null) return false;
        // Placement asks for a colour: the slot's linked type (the Wild slot takes a Wild)
        if (_stages[_stage].UseSlotType && s.Slot < _slotTypes.Length) return top == _slotTypes[s.Slot];
        return s.Required == CardType.Invalid || top == s.Required;
    }

    bool Eval(string pred, Step s) => pred switch
    {
        "AlwaysTrue" => true,
        "AlwaysFalse" => false,
        "HasCardInSlot" => HasCardInSlot(s),
        "LineComplete" => _started && DateTime.UtcNow >= _voEnds,
        "HasNoCards" => Enumerable.Range(0, 5).All(i => _slot(i).Count == 0),
        "HasAnyCards" => Enumerable.Range(0, 5).Any(i => _slot(i).Count > 0),
        "PopupClosed" or "CustomPopupClosed" => _popupClosed,
        "MixSaved" => _mixSaved,
        "HasStackedCards" => s.Slot is >= 0 and < 5 && _slot(s.Slot).Count >= 2,
        _ => false,
    };

    void Next()
    {
        while (true)
        {
            if (_step >= 0) Exit(Cur);
            _step++;
            if (_step >= _stages[_stage].Steps.Length) { _stage++; _step = 0; if (_stage >= _stages.Length) { Finish(); return; } }
            var s = Cur;
            _started = false; _popupClosed = false; _mixSaved = false; _stepEnteredAt = DateTime.UtcNow;
            if (Eval(s.SkipWhen, s)) continue;                  // e.g. the requested card is already there
            double delay = s.StartDelay;
            if (s.PlayEarly && DateTime.UtcNow - _lastCardAt < TimeSpan.FromSeconds(1.5)) { _stopVo(); delay = Math.Min(delay, 0.3); }
            _startAt = DateTime.UtcNow.AddSeconds(delay);
            Console.WriteLine($"walkthrough: {_stages[_stage].Name} {_step} {s.Vo ?? "(no line)"}");
            return;
        }
    }

    void Begin(Step s)
    {
        _started = true; _voEnds = DateTime.UtcNow;
        if (s.Vo != null) PlayVo(s.Vo);
        if (s.Text != null) _text = s.Text; else if (!s.UsePreviousText) _text = null;
        foreach (var e in s.Enable) _on.Add(e);
        _popup = s.Popup;
        _slotFx = s.UseSlotEffect ? s.Slot : -1;
        _nextTimeout = s.Timeout > 0 ? DateTime.UtcNow.AddSeconds(s.Timeout) : DateTime.MaxValue;
        _serial++;
    }

    void Exit(Step s)
    {
        foreach (var e in s.Disable) _on.Remove(e);
        if (s.Popup != null) _popup = null;
        _slotFx = -1;
        _serial++;
    }

    /// <summary>Runs the flow; call a few times a second.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            if (!Active || _step < 0) return;
            var s = Cur;
            if (!_started)
            {
                // a request already met during the delay never plays (the coroutine checks `complete` first)
                if (s.FinishWhen is not ("LineComplete" or "PopupClosed" or "CustomPopupClosed" or "MixSaved") && Eval(s.FinishWhen, s)) { Next(); return; }
                if (DateTime.UtcNow < _startAt) return;
                Begin(s);
            }
            if (Eval(s.FinishWhen, s)) { Next(); return; }
            if (DateTime.UtcNow >= _nextTimeout)
            {
                if (s.VoTimeout != null && DateTime.UtcNow >= _voEnds) PlayVo(s.VoTimeout);
                _nextTimeout = DateTime.UtcNow.AddSeconds(s.Timeout);
            }
        }
    }

    public object? View()
    {
        lock (_lock)
        {
            if (!Active) return null;
            bool showLast = _lastCard != null && (DateTime.UtcNow - _lastCardShown).TotalSeconds < _lastFull + _lastFade;
            var s = _step >= 0 ? Cur : null;
            return new
            {
                stage = _step >= 0 ? _stages[_stage].Name : "", step = _step, serial = _serial,
                text = _text, textValue = _text != null ? Loc.Get(_text) : null,
                popup = _popup, on = _on.ToArray(), slot = _slotFx,
                slotType = _slotFx is >= 0 && _slotFx < _slotTypes.Length ? _slotTypes[_slotFx].ToString() : null,
                lastCard = showLast ? _lastCard : null, lastCardId = _lastCardShown.Ticks, lastFull = _lastFull, lastFade = _lastFade,   // the table fades it
                skip = _stages[_stage].ShowSkip,
                speaking = _started && DateTime.UtcNow < _voEnds,
            };
        }
    }
}
