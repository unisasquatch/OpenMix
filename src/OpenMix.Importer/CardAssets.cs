using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenMix.Board;

namespace OpenMix.Importer;

/// <summary>Card content (art and music) for the cards in the user's collection, as the original app's card content
/// settings did it (Menu_Settings_CardCache): nothing downloads until the user chooses Menu_Settings_CardCacheDownloadCollection
/// and confirms the size.
/// Only collection entries whose board signature verifies count as owned. Sources, in order: a local folder the user
/// already has (<c>card_art/&lt;Image&gt;.png</c> bundles, <c>card_audio/&lt;Audio&gt;.arktoc</c>), else the game's card
/// server, which returns the same files the app downloaded. Art bundles are decoded with AssetStudio; music archives
/// are unpacked leaving out the encrypted .mogg stems (OpenMix doesn't decrypt them).</summary>
public sealed class CardAssets
{
    const string MetadataUrl = "https://dropmix-prod.hmxwebservices.com/1/and/cards/get_metadata/";
    /// <summary>Pause between requests to the card server, and the most retries after a "slow down" or server error.</summary>
    static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(250);
    const int MaxRetries = 4;

    readonly string _content, _work; readonly string? _localCache; readonly Action<string> _log;

    public CardAssets(string contentRoot, string work, string? localCache, Action<string> log)
    { _content = contentRoot; _work = work; _localCache = localCache; _log = log; }

    public sealed record CardFiles(int Cti, string Cid, string Image, string Audio);

    /// <summary>Why a download stopped, matching the original app's card-content errors.</summary>
    public enum Failure { None, NoInternet, MetadataFailed, DownloadFailed, SaveFailed, Cancelled }

    public sealed class DownloadError(Failure kind, string message) : Exception(message) { public Failure Kind { get; } = kind; }

    // ---------------------------------------------------------------- ownership

    /// <summary>Cards in data/collection.json whose stored board signature verifies against the cards' public key and
    /// whose UID belongs to that card. Anything else (hand-edited entries, simulated cards) doesn't count.</summary>
    public List<CardFiles> OwnedCards()
    {
        var all = AllCards();
        var byCti = all.GroupBy(c => c.Cti).ToDictionary(g => g.Key, g => g.First());
        var f = Path.Combine(_content, "data", "collection.json");
        var owned = new Dictionary<int, CardFiles>();
        if (!File.Exists(f)) return new();
        using var doc = JsonDocument.Parse(File.ReadAllText(f));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!ulong.TryParse(e.TryGetProperty("Uid", out var u) ? u.GetString() : null, System.Globalization.NumberStyles.HexNumber, null, out var uid)) continue;
            var sigHex = e.TryGetProperty("Sig", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(sigHex)) continue;
            byte[] sig; try { sig = Convert.FromHexString(sigHex); } catch (FormatException) { continue; }
            if (!CardSignature.VerifyCard(uid, sig)) continue;
            int cti = BoardProtocol.CtiFromUid(uid).Value;
            if (byCti.TryGetValue(cti, out var card)) owned[cti] = card;
        }
        return owned.Values.ToList();
    }

    public List<CardFiles> AllCards() =>
        Csv.Read(File.ReadAllText(Path.Combine(_content, "data", "cards.csv")))
           .Where(r => int.TryParse(r.GetValueOrDefault("CTI"), out _))
           .Select(r => new CardFiles(int.Parse(r["CTI"]), r["CID"], r.GetValueOrDefault("Image", ""), r.GetValueOrDefault("Audio", "")))
           .ToList();

    public bool HasContent(CardFiles c) =>
        (c.Image.Length == 0 || File.Exists(Path.Combine(_content, "cardassets", "card_png", c.Image + ".png")))
        && (c.Audio.Length == 0 || Directory.Exists(Path.Combine(_content, "audio_unpacked", c.Audio)));

    /// <summary>Bytes of card content stored on this PC (art and card music; not the game's own sounds).</summary>
    public long StoredBytes()
    {
        long n = 0;
        foreach (var d in new[] { Path.Combine(_content, "cardassets") }.Concat(SongDirs()))
            if (Directory.Exists(d)) n += Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        return n;
    }

    IEnumerable<string> SongDirs()
    {
        var root = Path.Combine(_content, "audio_unpacked");
        return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Where(d => Path.GetFileName(d) != "_obb") : Enumerable.Empty<string>();
    }

    /// <summary>Menu_Settings_CardCacheDeleteAll: removes downloaded art and card music (the collection stays).</summary>
    public void DeleteAll()
    {
        try { Directory.Delete(Path.Combine(_content, "cardassets"), true); } catch (DirectoryNotFoundException) { }
        foreach (var d in SongDirs().ToList()) Directory.Delete(d, true);
    }

    // ---------------------------------------------------------------- download

    HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenMix/0.1");
        return http;
    }

    /// <summary>Download size in bytes for the cards that still need content (for the confirm prompt).</summary>
    public async Task<long> EstimateAsync(IReadOnlyList<CardFiles> cards, CancellationToken ct)
    {
        // count each missing file on its own: a card may already have its art but not its music, or a local copy of one
        var need = cards.Where(c => !HasContent(c)).Select(c => (c, missing: Missing(c))).Where(x => x.missing.art || x.missing.music).ToList();
        if (need.Count == 0) return 0;
        using var http = NewClient();
        var meta = await FetchMetadata(http, need.Select(x => x.c.Cti), ct);
        return need.Sum(x => meta.TryGetValue(x.c.Cti, out var m)
            ? (x.missing.art ? Bytes(m, "image_bytes_and") : 0) + (x.missing.music ? Bytes(m, "audio_bytes") : 0) : 0);
    }

    /// <summary>Which of a card's files still have to come from the server (not installed, and not in the local folder).</summary>
    (bool art, bool music) Missing(CardFiles c)
    {
        var (localArt, localMusic) = Local(c);
        bool art = c.Image.Length > 0 && !File.Exists(Path.Combine(_content, "cardassets", "card_png", c.Image + ".png")) && localArt == null;
        bool music = c.Audio.Length > 0 && !Directory.Exists(Path.Combine(_content, "audio_unpacked", c.Audio)) && localMusic == null;
        return (art, music);
    }

    static long Bytes(JsonElement m, string k) => m.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : 0;

    (byte[]? art, byte[]? audio) Local(CardFiles c)
    {
        if (_localCache == null) return default;
        byte[]? R(string sub, string name) { var f = Path.Combine(_localCache, sub, name); return File.Exists(f) ? File.ReadAllBytes(f) : null; }
        var art = c.Image.Length > 0 ? R("card_art", c.Image + ".png") : null;
        var aud = c.Audio.Length > 0 ? R("card_audio", c.Audio + ".arktoc") : null;
        return art == null && aud == null ? default : (art, aud);
    }

    /// <summary>Fetches and installs content for <paramref name="cards"/> that don't have it yet, one file at a time.
    /// <paramref name="progress"/> gets (bytes done, bytes total). Throws <see cref="DownloadError"/>.</summary>
    public async Task<int> DownloadAsync(IReadOnlyList<CardFiles> cards, string assetStudio, Action<long, long>? progress, CancellationToken ct)
    {
        var need = cards.Where(c => !HasContent(c)).GroupBy(c => c.Cti).Select(g => g.First()).ToList();
        _log($"{cards.Count} cards selected, {need.Count} need content");
        if (need.Count == 0) return 0;
        var bundles = Path.Combine(_work, "card_art"); Directory.CreateDirectory(bundles);
        using var http = NewClient();
        Dictionary<int, JsonElement>? meta = null;
        long total = 0, done = 0; int installed = 0;
        foreach (var c in need)
        {
            ct.ThrowIfCancellationRequested();
            var (art, audio) = Local(c);
            var (needArt, needMusic) = Missing(c);          // only files neither installed nor in the local folder
            if (needArt || needMusic)
            {
                if (meta == null)
                {
                    meta = await FetchMetadata(http, need.Select(n => n.Cti), ct);
                    total = need.Sum(n => { var (a2, m2) = Missing(n); return meta.TryGetValue(n.Cti, out var mm) ? (a2 ? Bytes(mm, "image_bytes_and") : 0) + (m2 ? Bytes(mm, "audio_bytes") : 0) : 0; });
                    progress?.Invoke(done, total);
                }
                if (!meta.TryGetValue(c.Cti, out var m)) { _log($"  {c.Cid}: the card server has no files for it"); continue; }
                if (needArt) { art = await Download(http, m, "image_url_and", "image_checksum_and", c.Cid, ct); done += Bytes(m, "image_bytes_and"); progress?.Invoke(done, total); }
                if (needMusic) { audio = await Download(http, m, "audio_url", "audio_checksum", c.Cid, ct); done += Bytes(m, "audio_bytes"); progress?.Invoke(done, total); }
            }
            try
            {
                if (art != null) await File.WriteAllBytesAsync(Path.Combine(bundles, c.Image + ".png"), art, ct);
                if (audio != null) UnpackMusic(audio, c.Audio);
            }
            catch (IOException e) { throw new DownloadError(Failure.SaveFailed, e.Message); }
            installed++;
        }
        DecodeArt(bundles, Path.Combine(_content, "cardassets", "card_png"), assetStudio);
        return installed;
    }

    async Task<Dictionary<int, JsonElement>> FetchMetadata(HttpClient http, IEnumerable<int> ctis, CancellationToken ct)
    {
        var all = new Dictionary<int, JsonElement>();
        foreach (var batch in ctis.Distinct().Chunk(100))
        {
            using var r = await Send(http, () => new HttpRequestMessage(HttpMethod.Post, MetadataUrl)
            {
                Content = new FormUrlEncodedContent(new[] { KeyValuePair.Create("params", JsonSerializer.Serialize(new { card_ids = batch })) }),
            }, Failure.MetadataFailed, ct);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            foreach (var c in doc.RootElement.GetProperty("response").GetProperty("cards").EnumerateArray())
                all[c.GetProperty("card_id").GetInt32()] = c.Clone();
        }
        return all;
    }

    async Task<byte[]?> Download(HttpClient http, JsonElement m, string urlKey, string sumKey, string cid, CancellationToken ct)
    {
        if (!m.TryGetProperty(urlKey, out var u) || u.GetString() is not { Length: > 0 } url) return null;
        using var r = await Send(http, () => new HttpRequestMessage(HttpMethod.Get, url), Failure.DownloadFailed, ct);
        var data = await r.Content.ReadAsByteArrayAsync(ct);
        if (m.TryGetProperty(sumKey, out var s) && s.TryGetUInt32(out var want) && Checksum(data) != want)
        { _log($"  {cid}: {urlKey} failed its checksum, skipped"); return null; }
        return data;
    }

    /// <summary>One request at a time with a short pause before each. On "429 Too Many Requests" or a 5xx answer it waits
    /// (the server's Retry-After if given, else 2, 4, 8, 16 s) and tries again, up to <see cref="MaxRetries"/> times.</summary>
    async Task<HttpResponseMessage> Send(HttpClient http, Func<HttpRequestMessage> make, Failure onFail, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            await Task.Delay(Pace, ct);
            HttpResponseMessage r;
            try { r = await http.SendAsync(make(), ct); }
            catch (HttpRequestException e) when (e.InnerException is System.Net.Sockets.SocketException)
            { throw new DownloadError(Failure.NoInternet, e.Message); }
            catch (HttpRequestException e) { throw new DownloadError(onFail, e.Message); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { r = new HttpResponseMessage(HttpStatusCode.GatewayTimeout); }
            if (r.IsSuccessStatusCode) return r;
            bool retry = r.StatusCode == HttpStatusCode.TooManyRequests || (int)r.StatusCode >= 500;
            if (!retry || attempt >= MaxRetries) { var code = (int)r.StatusCode; r.Dispose(); throw new DownloadError(onFail, $"HTTP {code}"); }
            var wait = r.Headers.RetryAfter?.Delta ?? (r.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2 << attempt));
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            // never retry sooner than the server asked; if it asks for more than two minutes, stop and let the user retry later
            if (wait > TimeSpan.FromMinutes(2)) { var code = (int)r.StatusCode; r.Dispose(); throw new DownloadError(onFail, $"HTTP {code}, server asked to wait {wait.TotalMinutes:0} min"); }
            _log($"  card server asked to slow down ({(int)r.StatusCode}); waiting {wait.TotalSeconds:0} s");
            r.Dispose();
            await Task.Delay(wait, ct);
        }
    }

    /// <summary>The game's file checksum: XOR of the four big-endian 32-bit words of the MD5.</summary>
    public static uint Checksum(byte[] data)
    {
        var h = MD5.HashData(data); uint x = 0;
        for (int i = 0; i < 16; i += 4) x ^= (uint)(h[i] << 24 | h[i + 1] << 16 | h[i + 2] << 8 | h[i + 3]);
        return x;
    }

    /// <summary>.arktoc: u32 N, N × { u32 size, 7-bit-length UTF-8 path }, then the files back to back.</summary>
    void UnpackMusic(byte[] data, string audioId)
    {
        using var br = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        int n = br.ReadInt32();
        if (n < 0 || n > 10000) throw new InvalidDataException("bad music archive");
        var entries = new List<(string name, int size)>();
        for (int i = 0; i < n; i++) { int size = br.ReadInt32(); entries.Add((br.ReadString(), size)); }
        long pos = br.BaseStream.Position;
        if (entries.Any(e => e.size < 0) || pos + entries.Sum(e => (long)e.size) > data.Length) throw new InvalidDataException("bad music archive");
        var root = Path.GetFullPath(Path.Combine(_content, "audio_unpacked", audioId));
        foreach (var (name, size) in entries)
        {
            var dest = GameFiles.SafeJoin(root, name.Replace('\\', '/'));
            if (dest != null && !dest.EndsWith(".mogg", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, data.AsSpan((int)pos, size).ToArray());
            }
            pos += size;
        }
    }

    /// <summary>Art bundles hold one 1024² texture each: export it, keep the largest image, and make a 384² JPEG thumb.</summary>
    int DecodeArt(string bundles, string pngDir, string assetStudio)
    {
        var files = Directory.GetFiles(bundles, "*.png");
        if (files.Length == 0) return 0;
        var outDir = Path.Combine(_work, "card_art_export");
        AssetExport.RunTool(assetStudio, _log, bundles, "-t", "tex2d,sprite", "-g", "fileName", "--image-format", "png", "-o", outDir, "--log-level", "warning");
        var thumbDir = Path.Combine(_content, "cardassets", "card_thumb");
        Directory.CreateDirectory(pngDir); Directory.CreateDirectory(thumbDir);
        int n = 0;
        foreach (var b in files)
        {
            var exp = Path.Combine(outDir, Path.GetFileName(b) + "_export");
            if (!Directory.Exists(exp)) { _log($"  no texture in {Path.GetFileName(b)}"); continue; }
            var best = Directory.GetFiles(exp, "*.png", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            if (best == null) continue;
            var name = Path.GetFileNameWithoutExtension(b);
            File.Copy(best, Path.Combine(pngDir, name + ".png"), true);
            Thumb(best, Path.Combine(thumbDir, name + ".jpg"));
            n++;
        }
        _log($"card art: {n} images");
        return n;
    }

    static void Thumb(string png, string jpg)
    {
        using var src = new Bitmap(png);
        using var t = new Bitmap(384, 384, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(t))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Black); g.DrawImage(src, 0, 0, 384, 384);
        }
        var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1); p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        t.Save(jpg, enc, p);
    }
}
