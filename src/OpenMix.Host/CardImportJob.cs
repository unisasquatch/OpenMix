using OpenMix.Importer;

/// <summary>Card content (art and music) the way the original app's Settings → Menu_Settings_CardCache worked: nothing
/// downloads until the user picks Menu_Settings_CardCacheDownloadCollection, sees the download size and confirms. Progress, cancel, delete
/// and the original's error messages are exposed to the UI through <see cref="View"/>.</summary>
public sealed class CardContent
{
    readonly string _root;
    readonly object _lock = new();
    CancellationTokenSource? _cts;
    string _state = "idle";            // idle | estimating | confirm | downloading | error
    long _size, _done, _total; string? _error;
    List<CardAssets.CardFiles>? _selection;        // the cards the confirmed size was worked out for
    int _op;                                        // current operation; results from older (cancelled) ones are ignored
    public int Version { get; private set; }        // bumps after each download so pages reload card images
    public Action? Changed { get; set; }

    public CardContent(string root) { _root = root; }

    static string Work => Path.Combine(Path.GetTempPath(), "OpenMix-cards-" + Environment.ProcessId);

    CardAssets Assets() => new(_root, Work,
        Environment.GetEnvironmentVariable("OPENMIX_CARD_CACHE") is { Length: > 0 } c ? c : null, s => Console.WriteLine("cards: " + s));

    /// <summary>Light state for every table update: dialog state, error key and progress.</summary>
    public object View()
    {
        lock (_lock) return new
        {
            state = _state, error = _error, version = Version,
            sizeMB = Math.Max(1, Math.Round(_size / 1048576.0)), doneMB = Math.Round(_done / 1048576.0), totalMB = Math.Round(_total / 1048576.0),
        };
    }

    /// <summary>Settings → CARD CONTENT figures (reads the disk, so only on request).</summary>
    public object Details()
    {
        var a = Assets(); var owned = a.OwnedCards();
        return new { owned = owned.Count, downloaded = owned.Count(a.HasContent), storedMB = Math.Round(a.StoredBytes() / 1048576.0) };
    }

    void Set(Action a) { lock (_lock) a(); Changed?.Invoke(); }
    /// <summary>Apply a background result only if its operation is still the current one.</summary>
    void SetIf(int op, Action a) { lock (_lock) { if (op != _op) return; a(); } Changed?.Invoke(); }

    static string ErrorKey(CardAssets.Failure f) => f switch
    {
        CardAssets.Failure.NoInternet => "CardCacheError_NoInternet",
        CardAssets.Failure.MetadataFailed => "CardCacheError_MetadataFailed",
        CardAssets.Failure.SaveFailed => "CardCacheError_SaveFailed",
        _ => "CardCacheError_DownloadFailed",
    };

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
                var a = Assets(); var need = a.OwnedCards().Where(c => !a.HasContent(c)).ToList();
                if (need.Count == 0) { SetIf(op, () => _state = "idle"); return; }
                var size = await a.EstimateAsync(need, ct);
                SetIf(op, () => { _size = size; _selection = need; _state = "confirm"; });
            }
            catch (OperationCanceledException) { }
            catch (CardAssets.DownloadError e) { SetIf(op, () => { _state = "error"; _error = ErrorKey(e.Kind); }); }
            catch (Exception e) { Console.WriteLine("cards: " + e.Message); SetIf(op, () => { _state = "error"; _error = "CardCacheError_MetadataFailed"; }); }
        });
    }

    /// <summary>The user confirmed the size: download.</summary>
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
                await a.DownloadAsync(sel, studio!, (d, t) => SetIf(op, () => { _done = d; _total = t; }), ct);
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
        Set(() => Version++);
    }
}
