using System.Threading.Channels;
using OpenMix.Importer;

/// <summary>Card content (art) fetched from the card server. Two paths:
/// <list type="bullet">
/// <item>The <b>automatic</b> pull (<see cref="AutoPull"/> on startup, or <see cref="QueueArt"/> when a card is placed)
/// runs in the background through a queue, decodes art in small batches so it appears progressively, and reports a
/// non-blocking progress bar (bgDone/bgTotal) — the app stays usable while it runs.</item>
/// <item>The <b>manual</b> "download my collection" keeps the original size-confirm prompt and blocking overlay.</item>
/// </list>
/// Downloads are art-only: audio only ever comes from the import.</summary>
public sealed class CardContent
{
    readonly string _root;
    readonly object _lock = new();

    // manual flow (Settings → download my collection): blocking overlay
    CancellationTokenSource? _cts;
    string _state = "idle";            // idle | estimating | confirm | downloading | error
    long _size, _done, _total; string? _error;
    List<CardAssets.CardFiles>? _selection;
    int _op;

    // background art pull (non-blocking progress bar), fed by a queue of CTIs
    readonly Channel<int> _queue = Channel.CreateUnbounded<int>();
    readonly HashSet<int> _queued = new();
    bool _worker, _bg; int _bgDone, _bgTotal;

    public int Version { get; private set; }            // bumps after each decode so pages reload card images
    public Action? Changed { get; set; }

    /// <summary>"all" = pull art for the whole catalog; "collection" = only cards in data/collection.json.</summary>
    public string Scope { get; set; } = "all";
    /// <summary>False until the user has chosen a download scope — the UI shows the first-run prompt while it is false.</summary>
    public bool ScopeChosen { get; set; }

    public CardContent(string root)
    {
        _root = root;
        // Start the art-version from how much art is already on disk, so it's non-zero once content exists and changes as
        // content grows. /art image URLs carry ?v=version; a non-zero, content-derived version evicts any stale 404 a
        // client cached from before the art was downloaded (an old build cached /art 404s for a day).
        try { Version = Directory.EnumerateFiles(Path.Combine(_root, "cardassets", "card_png")).Count() + Directory.EnumerateFiles(Path.Combine(_root, "cardassets", "card_thumb")).Count(); }
        catch { }
    }

    List<CardAssets.CardFiles> ScopeCards(CardAssets a) => Scope == "collection" ? a.CollectionCards() : a.AllCards();

    static string Work => Path.Combine(Path.GetTempPath(), "OpenMix-cards-" + Environment.ProcessId);
    CardAssets Assets(string? sub = null) => new(_root, sub == null ? Work : Work + sub,
        Environment.GetEnvironmentVariable("OPENMIX_CARD_CACHE") is { Length: > 0 } c ? c : null, s => Console.WriteLine("cards: " + s));

    /// <summary>Light state for every table update: the manual dialog/overlay state plus the background bar.</summary>
    public object View()
    {
        lock (_lock) return new
        {
            state = _state, error = _error, version = Version, prompt = !ScopeChosen,
            sizeMB = Math.Max(1, Math.Round(_size / 1048576.0)), doneMB = Math.Round(_done / 1048576.0), totalMB = Math.Round(_total / 1048576.0),
            bg = _bg, bgDone = _bgDone, bgTotal = _bgTotal, bgPct = _bgTotal > 0 ? (int)Math.Round(100.0 * _bgDone / _bgTotal) : 0,
        };
    }

    /// <summary>Settings → CARD CONTENT figures (reads the disk, so only on request).</summary>
    public object Details()
    {
        var a = Assets(); var pool = ScopeCards(a);
        return new { owned = pool.Count, downloaded = pool.Count(a.HasContent), storedMB = Math.Round(a.StoredBytes() / 1048576.0) };
    }

    void SetIf(int op, Action a) { lock (_lock) { if (op != _op) return; a(); } Changed?.Invoke(); }

    static string ErrorKey(CardAssets.Failure f) => f switch
    {
        CardAssets.Failure.NoInternet => "CardCacheError_NoInternet",
        CardAssets.Failure.MetadataFailed => "CardCacheError_MetadataFailed",
        CardAssets.Failure.SaveFailed => "CardCacheError_SaveFailed",
        _ => "CardCacheError_DownloadFailed",
    };

    // ============================================================ background art pull

    /// <summary>Queue card CTIs to fetch art for in the background (non-blocking, shows the progress bar). CTIs whose art
    /// is already present or that are already queued are skipped. Used on card placement and by <see cref="AutoPull"/>.</summary>
    public void QueueArt(IEnumerable<int> ctis)
    {
        if (ContentPaths.AssetStudio() == null) return;
        bool changed = false;
        lock (_lock)
        {
            foreach (var cti in ctis)
                if (_queued.Add(cti)) { _queue.Writer.TryWrite(cti); _bgTotal++; changed = true; }
            if (changed) { _bg = true; StartWorker(); }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Queue the whole current scope's missing art (startup, after an import, or after the scope changes).</summary>
    public void AutoPull()
    {
        if (ContentPaths.AssetStudio() == null) return;
        _ = Task.Run(() =>
        {
            try { var a = Assets(); QueueArt(ScopeCards(a).Where(c => !a.HasContent(c)).Select(c => c.Cti)); }
            catch (Exception e) { Console.WriteLine("cards: " + e.Message); }
        });
    }

    void StartWorker()
    {
        if (_worker) return; _worker = true;
        _ = Task.Run(async () =>
        {
            var studio = ContentPaths.AssetStudio();
            while (await _queue.Reader.WaitToReadAsync())
            {
                var batch = new List<int>();
                while (batch.Count < 24 && _queue.Reader.TryRead(out var cti)) batch.Add(cti);
                if (batch.Count == 0) continue;
                if (studio != null)
                    try
                    {
                        var a = Assets("-bg");
                        var cards = a.AllCards().Where(c => batch.Contains(c.Cti) && !a.HasContent(c)).ToList();
                        if (cards.Count > 0)
                            await a.DownloadAsync(cards, studio, null, CancellationToken.None, artOnly: false,
                                onDecoded: () => { lock (_lock) Version++; Changed?.Invoke(); });
                    }
                    catch (Exception e) { Console.WriteLine("cards: art " + e.Message); }
                bool done;
                lock (_lock)
                {
                    _bgDone += batch.Count; Version++;   // bump every batch (art or audio) so pages cache-bust /art images
                    done = _bgDone >= _bgTotal;
                    if (done) { _bg = false; _bgDone = 0; _bgTotal = 0; _queued.Clear(); }
                }
                Changed?.Invoke();
                if (done) { try { Directory.Delete(Work + "-bg", true); } catch { } }
            }
            lock (_lock) _worker = false;
        });
    }

    // ============================================================ manual flow (Settings → download my collection)

    /// <summary>Menu_Settings_CardCacheDownloadCollection pressed: work out the size for the confirm prompt.</summary>
    public void Prepare()
    {
        CancellationToken ct; int op;
        lock (_lock) { if (_state is "estimating" or "downloading") return; _state = "estimating"; _error = null; _cts = new CancellationTokenSource(); ct = _cts.Token; op = ++_op; }
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            try
            {
                var a = Assets(); var need = ScopeCards(a).Where(c => !a.HasContent(c)).ToList();
                if (need.Count == 0) { SetIf(op, () => _state = "idle"); return; }
                var size = await a.EstimateAsync(need, ct, artOnly: false);
                SetIf(op, () => { _size = size; _selection = need; _state = "confirm"; });
            }
            catch (OperationCanceledException) { }
            catch (CardAssets.DownloadError e) { SetIf(op, () => { _state = "error"; _error = ErrorKey(e.Kind); }); }
            catch (Exception e) { Console.WriteLine("cards: " + e.Message); SetIf(op, () => { _state = "error"; _error = "CardCacheError_MetadataFailed"; }); }
        });
    }

    /// <summary>The user confirmed the size: download (blocking overlay, art decoded in batches as it arrives).</summary>
    public void Confirm()
    {
        var studio = ContentPaths.AssetStudio();
        CancellationToken ct; List<CardAssets.CardFiles> sel; int op;
        lock (_lock)
        {
            if (_state != "confirm") return;
            if (studio == null) { _state = "error"; _error = "CardCacheError_SaveFailed"; return; }
            _state = "downloading"; _done = 0; _total = _size; _cts = new CancellationTokenSource(); ct = _cts.Token;
            sel = _selection ?? new(); op = ++_op;
        }
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            var a = Assets();
            try
            {
                await a.DownloadAsync(sel, studio!, (d, t) => SetIf(op, () => { _done = d; _total = t; }), ct, artOnly: false,
                    onDecoded: () => { lock (_lock) Version++; Changed?.Invoke(); });
                SetIf(op, () => { _state = "idle"; Version++; });
            }
            catch (OperationCanceledException) { SetIf(op, () => { _state = "idle"; Version++; }); }
            catch (CardAssets.DownloadError e) { Console.WriteLine("cards: " + e.Message); SetIf(op, () => { _state = "error"; _error = ErrorKey(e.Kind); Version++; }); }
            catch (Exception e) { Console.WriteLine("cards: " + e.Message); SetIf(op, () => { _state = "error"; _error = "CardCacheError_SaveFailed"; Version++; }); }
            finally { try { Directory.Delete(Work, true); } catch { } }
        });
    }

    /// <summary>CardCacheOverlay_TapToCancel, "No" on the confirm prompt, or closing the error.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            if (_state is "confirm" or "error" or "estimating") { _state = "idle"; _op++; }   // a pending size check no longer counts
            _error = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Menu_Settings_CardCacheDeleteAll (the UI asks first).</summary>
    public void DeleteAll()
    {
        lock (_lock) if (_state is "downloading" or "estimating") return;
        Assets().DeleteAll();
        lock (_lock) Version++;
        Changed?.Invoke();
    }
}
