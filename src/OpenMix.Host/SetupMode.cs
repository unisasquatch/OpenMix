using System.Diagnostics;
using OpenMix.Importer;

/// <summary>First run: no game content yet. Serves setup.html, which collects the user's own APK and OBB (uploaded
/// from disk or pulled from their phone over adb), runs the importer, then restarts OpenMix into the game.</summary>
static class SetupMode
{
    static readonly object Lock = new();
    static readonly List<string> Log = new();
    static string _state = "idle";          // idle | pulling | importing | done | failed
    static bool _restart;

    static string ImportDir => Path.Combine(Path.GetDirectoryName(ContentPaths.Default)!, "import");
    static string ApkPath => Path.Combine(ImportDir, "game.apk");
    static string ObbPath => Path.Combine(ImportDir, "game.obb");

    static void Say(string s) { lock (Lock) { Log.Add(s); if (Log.Count > 400) Log.RemoveAt(0); } Console.WriteLine("setup: " + s); }

    public static async Task RunAsync(int port, bool noWindow, string[] args, Action<string, IHostApplicationLifetime> window)
    {
        Directory.CreateDirectory(ImportDir);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = ContentPaths.WebRoot() });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024);
        var app = builder.Build();
        HostGuard.Use(app, allowPhoneRoutes: false);
        app.UseStaticFiles();
        app.MapGet("/", () => Results.Redirect("/setup.html"));
        app.MapGet("/table.html", () => Results.Redirect("/setup.html"));

        app.MapGet("/api/setup/status", () =>
        {
            lock (Lock) return Results.Json(new
            {
                state = _state, log = Log.TakeLast(60).ToArray(),
                apk = File.Exists(ApkPath) ? new FileInfo(ApkPath).Length : 0,
                obb = File.Exists(ObbPath) ? new FileInfo(ObbPath).Length : 0,
                assetStudio = AssetStudio() != null && ContentPaths.Il2CppDumper() != null, adb = Adb() != null, content = ContentPaths.Default,
            });
        });

        // the file's bytes as the request body (setup.html sends the File object directly)
        app.MapPost("/api/setup/upload/{kind}", async (string kind, HttpRequest req) =>
        {
            if (kind is not ("apk" or "obb")) return Results.BadRequest();
            var dst = kind == "apk" ? ApkPath : ObbPath;
            await using (var f = File.Create(dst)) await req.Body.CopyToAsync(f);
            if (!LooksLikeZip(dst)) { File.Delete(dst); return Results.BadRequest(new { error = $"That file isn't an {kind.ToUpper()} (not a zip archive)." }); }
            Say($"got the {kind.ToUpper()} ({new FileInfo(dst).Length / 1048576} MB)");
            return Results.Ok();
        });

        app.MapPost("/api/setup/adb", () =>
        {
            lock (Lock) { if (_state is "pulling" or "importing") return Results.Conflict(); _state = "pulling"; }
            _ = Task.Run(() =>
            {
                try { PullFromPhone(); lock (Lock) _state = "idle"; }
                catch (Exception e) { Say("phone: " + e.Message); lock (Lock) _state = "idle"; }
            });
            return Results.Ok();
        });

        app.MapPost("/api/setup/start", () =>
        {
            var studio = AssetStudio();
            if (!File.Exists(ApkPath) || !File.Exists(ObbPath)) return Results.BadRequest(new { error = "Add both the APK and the OBB first." });
            if (studio == null || ContentPaths.Il2CppDumper() == null) return Results.BadRequest(new { error = "OpenMix's tools folder is incomplete (AssetStudioModCLI or Il2CppDumper is missing). Reinstall OpenMix." });
            lock (Lock) { if (_state is "pulling" or "importing") return Results.Conflict(); _state = "importing"; }
            _ = Task.Run(() =>
            {
                try
                {
                    var work = Path.Combine(Path.GetTempPath(), "OpenMix-import-" + Environment.ProcessId);
                    new OpenMix.Importer.Importer(new ImportOptions(ApkPath, ObbPath, ContentPaths.Default, work) { AssetStudio = studio, Il2CppDumper = ContentPaths.Il2CppDumper() }, Say).Run();
                    lock (Lock) _state = "done";
                }
                catch (Exception e) { Say("import failed: " + e.Message); lock (Lock) _state = "failed"; }
            });
            return Results.Ok();
        });

        app.MapPost("/api/setup/finish", (IHostApplicationLifetime life) =>
        {
            lock (Lock) if (_state != "done") return Results.BadRequest();
            _restart = true;
            _ = Task.Run(async () => { await Task.Delay(300); life.StopApplication(); });
            return Results.Ok();
        });

        Say("waiting for your game files");
        if (noWindow) Console.WriteLine($"Setup: {HostGuard.WindowUrl($"http://localhost:{port}/setup.html")}");
        if (noWindow) await app.RunAsync();
        else
        {
            await app.StartAsync();
            window(HostGuard.WindowUrl($"http://localhost:{port}/setup.html"), app.Lifetime);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await app.StopAsync(stop.Token); } catch { }
        }
        if (_restart)
        {
            try { File.Delete(ApkPath); File.Delete(ObbPath); } catch { }
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        Environment.Exit(0);
    }

    static bool LooksLikeZip(string f)
    {
        using var s = File.OpenRead(f); var b = new byte[4];
        return s.Read(b, 0, 4) == 4 && b[0] == 'P' && b[1] == 'K';
    }

    static string? AssetStudio() => ContentPaths.AssetStudio();

    static string? Adb()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "tools", "platform-tools", "adb.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir.Trim(), "adb.exe"))) return Path.Combine(dir.Trim(), "adb.exe");
        return null;
    }

    /// <summary>Copies the installed DropMix app and its OBB off the user's phone (USB debugging on, adb only, no root).</summary>
    static void PullFromPhone()
    {
        var adb = Adb() ?? throw new InvalidOperationException("adb isn't installed (Android platform-tools).");
        var serials = RunAdb(adb, "devices").Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.EndsWith("\tdevice") || l.EndsWith(" device"))
            .Select(l => l.Split('\t', ' ')[0]).ToList();
        if (serials.Count == 0) throw new InvalidOperationException("no phone found. Plug it in, turn on USB debugging and allow this computer.");
        // one phone can show up twice (USB and wireless debugging): group by its hardware serial number
        var phones = serials.GroupBy(s => Shell(adb, s, "getprop", "ro.serialno").Trim()).ToList();
        if (phones.Count > 1) throw new InvalidOperationException("more than one phone is connected; unplug the others.");
        var dev = phones[0].First();

        var paths = Shell(adb, dev, "pm", "path", "com.hasbro.dropmix").Split('\n')
            .Select(l => l.Trim()).Where(l => l.StartsWith("package:")).Select(l => l[8..]).ToList();
        var apk = paths.FirstOrDefault(p => p.EndsWith("/base.apk")) ?? paths.FirstOrDefault()
                  ?? throw new InvalidOperationException("DropMix isn't installed on this phone.");
        Say("copying DropMix from the phone…");
        RunAdb(adb, "-s", dev, "pull", apk, ApkPath);
        string? obb = null;
        foreach (var dir in new[] { "/sdcard/Android/obb/com.hasbro.dropmix", "/sdcard/Download/Android/obb/com.hasbro.dropmix" })
        {
            string list; try { list = Shell(adb, dev, "ls", dir + "/"); } catch { continue; }
            var name = list.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("main.") && l.EndsWith(".obb")).OrderByDescending(l => l).FirstOrDefault();
            if (name != null) { obb = dir + "/" + name; break; }
        }
        if (obb == null) throw new InvalidOperationException("the DropMix OBB isn't on this phone (open DropMix once so it downloads).");
        Say("copying the OBB…");
        RunAdb(adb, "-s", dev, "pull", obb, ObbPath);
        Say("got both files from the phone");
    }

    static string Shell(string adb, string dev, params string[] cmd) => RunAdb(adb, new[] { "-s", dev, "shell" }.Concat(cmd).ToArray());

    static string RunAdb(string adb, params string[] a)
    {
        var psi = new ProcessStartInfo(adb) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in a) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"adb {a[0]} failed: {(e.Trim().Length > 0 ? e.Trim() : o.Trim())}");
        return o.Replace("\r", "");
    }
}
