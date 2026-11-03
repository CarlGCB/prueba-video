using System.Diagnostics;
using System.Text;
using static VideoCutter.Core.FfmpegTools;

namespace VideoCutter.Core;

public enum ExportMode { Copy, Reencode }

/// <summary>Estado de la exportación, pensado para mostrar la cuenta atrás.</summary>
public sealed record ExportProgress(string Phase, int ClipIndex, int ClipCount, double ProcessedSec, double TotalSec,
                                    double? EtaSec, double Speed, bool Joining);

public sealed class Exporter
{
    readonly FfmpegTools _t;
    public Exporter(FfmpegTools t) { _t = t; }

    /// <summary>Recorta cada vídeo a su tramo y los une, en el orden de la lista, en un solo MP4.</summary>
    public async Task RunAsync(IReadOnlyList<Clip> clips, string output, ExportMode mode, VideoEncoder enc, int crf,
                               IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (clips.Count == 0) throw new InvalidOperationException("No hay vídeos para exportar.");
        foreach (var c in clips)
            if (c.End <= c.Start) throw new InvalidOperationException($"En «{c.Name}», el fin debe ser mayor que el inicio.");
        bool anyAudio = clips.Any(c => c.Info.HasAudio), allAudio = clips.All(c => c.Info.HasAudio);
        if (mode == ExportMode.Copy && anyAudio && !allAudio)
            throw new InvalidOperationException("Para copiar sin recodificar, todos los vídeos deben tener audio, o ninguno. Usa «Recodificar».");

        var first = clips[0].Info;
        int W = first.Width - first.Width % 2, H = first.Height - first.Height % 2;
        double fps = first.Fps > 0 ? first.Fps : 25; int gop = (int)Math.Round(fps * 2);
        int n = clips.Count; double total = clips.Sum(c => c.Length);

        var temp = Path.Combine(Path.GetTempPath(), "VideoCutter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var tmpOut = output + ".tmp";
        var sw = Stopwatch.StartNew(); double done = 0;

        void Report(string phase, int idx, double processed, double speed, bool joining)
        {
            double el = sw.Elapsed.TotalSeconds, sp = processed > 1 && el > 2 ? processed / el : 0;
            double? eta = !joining && sp > 0 ? (total - processed) / sp + 3 : null;
            progress?.Report(new ExportProgress(phase, idx, n, processed, total, eta, sp, joining));
        }

        try
        {
            var parts = new List<string>();
            for (int i = 0; i < n; i++)
            {
                var c = clips[i]; var part = Path.Combine(temp, $"part{i:D3}.mp4"); double len = c.Length; int idx = i;
                string verb = mode == ExportMode.Reencode ? "Recodificando" : "Recortando";
                Report($"{verb} vídeo {i + 1} de {n}: {c.Name}", i, done, 0, false);
                Action<double, double> onP = (sec, sp) => Report($"{verb} vídeo {idx + 1} de {n}: {c.Name}", idx, done + Math.Min(sec, len), sp, false);

                var res = await _t.RunAsync(CutArgs(c, part, mode, enc, crf, W, H, fps, gop, anyAudio), onP, ct);
                if (!res.Ok && mode == ExportMode.Reencode && enc != VideoEncoder.Cpu)
                {
                    Report("El codificador de hardware falló; se usa la CPU…", i, done, 0, false);
                    res = await _t.RunAsync(CutArgs(c, part, mode, VideoEncoder.Cpu, crf, W, H, fps, gop, anyAudio), onP, ct);
                }
                if (!res.Ok) throw new InvalidOperationException($"FFmpeg no pudo procesar «{c.Name}»: {res.Error}");
                parts.Add(part); done += len;
            }

            var list = Path.Combine(temp, "list.txt");
            File.WriteAllLines(list, parts.Select(p => $"file '{p.Replace('\\', '/').Replace("'", "'\\''")}'"), new UTF8Encoding(false));
            Report("Uniendo todo en un solo MP4…", n, total, 0, true);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var j = await _t.RunAsync(new[] { "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-movflags", "+faststart", "-f", "mp4", tmpOut }, null, ct);
            if (!j.Ok) throw new InvalidOperationException($"FFmpeg no pudo unir los vídeos (¿códecs distintos? prueba «Recodificar»): {j.Error}");
            File.Move(tmpOut, output, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
            try { if (File.Exists(tmpOut)) File.Delete(tmpOut); } catch { }
        }
    }

    static List<string> CutArgs(Clip c, string part, ExportMode mode, VideoEncoder enc, int crf, int W, int H, double fps, int gop, bool anyAudio)
    {
        var a = new List<string> { "-ss", Inv(c.Start), "-i", c.Info.Path };
        bool silent = mode == ExportMode.Reencode && anyAudio && !c.Info.HasAudio;       // se añade audio mudo para poder unirlo
        if (silent) a.AddRange(new[] { "-f", "lavfi", "-t", Inv(c.Length), "-i", "anullsrc=channel_layout=stereo:sample_rate=48000" });
        a.AddRange(new[] { "-t", Inv(c.Length), "-map", "0:v:0" });
        if (mode == ExportMode.Copy)
            a.AddRange(new[] { "-map", "0:a:0?", "-c", "copy", "-avoid_negative_ts", "make_zero" });
        else
        {
            if (anyAudio) a.AddRange(new[] { "-map", silent ? "1:a:0" : "0:a:0" });
            a.AddRange(new[] { "-vf", $"scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2,fps={Inv(fps)},format=yuv420p" });
            a.AddRange(VideoArgs(enc, crf, gop));
            if (anyAudio) a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-af", "aresample=async=1:first_pts=0" });
        }
        a.AddRange(new[] { "-f", "mp4", part });
        return a;
    }
}

/// <summary>Convierte cualquier vídeo a MP4 (H.264 + AAC). Copia si ya es compatible; si no, recodifica.</summary>
public sealed class Converter
{
    readonly FfmpegTools _t;
    public Converter(FfmpegTools t) { _t = t; }

    public static bool CanCopy(MediaInfo i) => i.VCodec == "h264" && (!i.HasAudio || i.ACodec is "aac" or "mp3");

    /// <returns>"copiado" o "recodificado".</returns>
    public async Task<string> ToMp4Async(MediaInfo src, string outPath, VideoEncoder enc, int crf, Action<double, double>? onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var tmp = outPath + ".tmp";
        try
        {
            RunResult? r = null; string how = "recodificado";
            if (CanCopy(src))
            {
                r = await _t.RunAsync(new[] { "-i", src.Path, "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-movflags", "+faststart", "-f", "mp4", tmp }, onProgress, ct);
                if (r.Ok) how = "copiado";
            }
            if (r == null || !r.Ok)
            {
                double fps = src.Fps > 0 ? src.Fps : 25; int gop = (int)Math.Round(fps * 2);
                RunResult Enc(VideoEncoder e) => _t.RunAsync(BuildEncode(src, tmp, e, crf, gop), onProgress, ct).GetAwaiter().GetResult();
                r = await _t.RunAsync(BuildEncode(src, tmp, enc, crf, gop), onProgress, ct);
                if (!r.Ok && enc != VideoEncoder.Cpu) r = await _t.RunAsync(BuildEncode(src, tmp, VideoEncoder.Cpu, crf, gop), onProgress, ct);
            }
            if (!r!.Ok) throw new InvalidOperationException($"No se pudo convertir «{Path.GetFileName(src.Path)}»: {r.Error}");
            File.Move(tmp, outPath, overwrite: true);
            return how;
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    static List<string> BuildEncode(MediaInfo src, string tmp, VideoEncoder enc, int crf, int gop)
    {
        var a = new List<string> { "-i", src.Path, "-map", "0:v:0", "-map", "0:a:0?", "-pix_fmt", "yuv420p" };
        a.AddRange(VideoArgs(enc, crf, gop));
        if (src.HasAudio) a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        a.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", tmp });
        return a;
    }
}
