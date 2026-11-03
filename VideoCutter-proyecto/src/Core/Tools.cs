using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace VideoCutter.Core;

public sealed record RunResult(int ExitCode, string Error) { public bool Ok => ExitCode == 0; }

public enum VideoEncoder { Cpu, Nvenc, Qsv, Amf }

/// <summary>Localiza y ejecuta ffmpeg / ffprobe.</summary>
public sealed class FfmpegTools
{
    public string Ffmpeg { get; }
    public string Ffprobe { get; }
    public FfmpegTools(string ffmpeg, string ffprobe) { Ffmpeg = ffmpeg; Ffprobe = ffprobe; }

    public static string Inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static FfmpegTools? Locate(params string[] extraDirs)
    {
        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var d in extraDirs.Where(x => !string.IsNullOrEmpty(x)).Concat(path))
        {
            var f = Path.Combine(d, "ffmpeg" + ext); var p = Path.Combine(d, "ffprobe" + ext);
            if (File.Exists(f) && File.Exists(p)) return new FfmpegTools(f, p);
        }
        return null;
    }

    /// <summary>Ejecuta ffmpeg. onProgress recibe (segundos de salida procesados, velocidad).</summary>
    public async Task<RunResult> RunAsync(IEnumerable<string> args, Action<double, double>? onProgress = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var err = new Queue<string>();
        p.Start();
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });

        var errTask = Task.Run(async () =>
        {
            string? l;
            while ((l = await p.StandardError.ReadLineAsync()) != null) lock (err) { err.Enqueue(l); if (err.Count > 12) err.Dequeue(); }
        });
        var outTask = Task.Run(async () =>
        {
            string? l; double sec = 0, speed = 0;
            while ((l = await p.StandardOutput.ReadLineAsync()) != null)
            {
                int i = l.IndexOf('='); if (i < 0) continue;
                var k = l[..i]; var v = l[(i + 1)..].Trim();
                if ((k == "out_time_us" || k == "out_time_ms") && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var us)) sec = Math.Max(0, us / 1e6);
                else if (k == "speed" && double.TryParse(v.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sp)) speed = sp;
                else if (k == "progress") onProgress?.Invoke(sec, speed);
            }
        });
        await p.WaitForExitAsync();
        await Task.WhenAll(outTask, errTask);
        ct.ThrowIfCancellationRequested();
        string msg; lock (err) msg = string.Join(" | ", err);
        return new RunResult(p.ExitCode, msg);
    }

    static async Task<(int Code, string Out)> CaptureAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.Start();
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await o);
    }

    static double Rate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var t = s.Split('/');
        if (t.Length == 2 && double.TryParse(t[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0) return a / b;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var (code, json) = await CaptureAsync(Ffprobe, new[] { "-v", "error", "-print_format", "json", "-show_streams", "-show_format", path }, ct);
        if (code != 0 || string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException($"No se pudo leer «{System.IO.Path.GetFileName(path)}». ¿Es un vídeo válido?");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double dur = 0; long size = 0;
        if (root.TryGetProperty("format", out var f))
        {
            if (f.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dd)) dur = dd;
            if (f.TryGetProperty("size", out var sz) && long.TryParse(sz.GetString(), out var ss)) size = ss;
        }
        string? vc = null, ac = null; int w = 0, h = 0, sr = 0; double fps = 0;
        if (root.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var ct2) ? ct2.GetString() : null;
                var name = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                if (type == "video" && vc == null)
                {
                    if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1) continue; // carátula
                    vc = name;
                    w = s.TryGetProperty("width", out var ww) ? ww.GetInt32() : 0;
                    h = s.TryGetProperty("height", out var hh) ? hh.GetInt32() : 0;
                    fps = Rate(s.TryGetProperty("avg_frame_rate", out var af) ? af.GetString() : null);
                    if (fps <= 0) fps = Rate(s.TryGetProperty("r_frame_rate", out var rf) ? rf.GetString() : null);
                }
                else if (type == "audio" && ac == null)
                {
                    ac = name;
                    if (s.TryGetProperty("sample_rate", out var sre) && int.TryParse(sre.GetString(), out var srv)) sr = srv;
                }
            }
        if (vc == null) throw new InvalidOperationException($"«{System.IO.Path.GetFileName(path)}» no contiene vídeo.");
        return new MediaInfo(path, dur, vc, w, h, fps, ac, sr, size);
    }

    /// <summary>Codificadores de hardware que esta versión de FFmpeg incluye (no garantiza que haya tarjeta compatible).</summary>
    public async Task<HashSet<VideoEncoder>> ListEncodersAsync()
    {
        var set = new HashSet<VideoEncoder> { VideoEncoder.Cpu };
        try
        {
            var (_, o) = await CaptureAsync(Ffmpeg, new[] { "-hide_banner", "-encoders" }, CancellationToken.None);
            if (o.Contains(" h264_nvenc ")) set.Add(VideoEncoder.Nvenc);
            if (o.Contains(" h264_qsv ")) set.Add(VideoEncoder.Qsv);
            if (o.Contains(" h264_amf ")) set.Add(VideoEncoder.Amf);
        }
        catch { }
        return set;
    }

    public static string[] VideoArgs(VideoEncoder enc, int crf, int gop)
    {
        var c = crf.ToString(CultureInfo.InvariantCulture); var g = gop.ToString(CultureInfo.InvariantCulture);
        return enc switch
        {
            VideoEncoder.Nvenc => new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", c, "-b:v", "0", "-g", g },
            VideoEncoder.Qsv   => new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", c, "-g", g },
            VideoEncoder.Amf   => new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", c, "-qp_p", c, "-g", g },
            _                  => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", c, "-g", g, "-keyint_min", g, "-sc_threshold", "0" },
        };
    }

    /// <summary>Miniatura (un fotograma) del vídeo. Devuelve false si falla.</summary>
    public async Task<bool> ThumbnailAsync(string video, string jpg, double atSec, CancellationToken ct = default)
    {
        var r = await RunAsync(new[] { "-ss", Inv(atSec), "-i", video, "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "4", jpg }, null, ct);
        return r.Ok && File.Exists(jpg);
    }
}
