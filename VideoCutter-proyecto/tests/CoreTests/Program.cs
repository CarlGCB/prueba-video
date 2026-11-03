using System.Diagnostics;
using System.Globalization;
using VideoCutter.Core;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; Console.WriteLine($"{(ok ? "  OK " : "FALLA")} {name} {detail}"); }

string Sh(string exe, params string[] args)
{
    var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); return o;
}
void Gen(string name, params string[] args) => Sh("ffmpeg", new[] { "-loglevel", "error", "-y" }.Concat(args).Append(name).ToArray());

var dir = Path.Combine(Path.GetTempPath(), "vc_tests"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
string P(string f) => Path.Combine(dir, f);

Console.WriteLine("Generando vídeos de prueba…");
Gen(P("A.mp4"), "-f", "lavfi", "-i", "testsrc=d=10:s=320x240:r=25", "-f", "lavfi", "-i", "sine=d=10", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "25", "-c:a", "aac", "-shortest");
Gen(P("B.mp4"), "-f", "lavfi", "-i", "testsrc=d=8:s=320x240:r=25", "-f", "lavfi", "-i", "sine=f=500:d=8", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "25", "-c:a", "aac", "-shortest");
Gen(P("D.mp4"), "-f", "lavfi", "-i", "testsrc2=d=6:s=640x360:r=30", "-f", "lavfi", "-i", "sine=f=440:d=6", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
Gen(P("S.mp4"), "-f", "lavfi", "-i", "testsrc=d=5:s=320x240:r=25", "-c:v", "libx264", "-pix_fmt", "yuv420p");           // sin audio
Gen(P("X.avi"), "-f", "lavfi", "-i", "testsrc=d=4:s=320x240:r=25", "-f", "lavfi", "-i", "sine=d=4", "-c:v", "mpeg4", "-c:a", "mp2", "-shortest");
Gen(P("H.mkv"), "-f", "lavfi", "-i", "testsrc=d=4:s=320x240:r=25", "-f", "lavfi", "-i", "sine=d=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");

var tools = FfmpegTools.Locate()!;
Check("ffmpeg y ffprobe localizados", tools != null);

// 1) Lectura de datos
var a = await tools.ProbeAsync(P("A.mp4")); var s = await tools.ProbeAsync(P("S.mp4")); var d = await tools.ProbeAsync(P("D.mp4"));
Check("Probe A: h264 320x240 25fps aac ~10 s", a.VCodec == "h264" && a.Width == 320 && a.Height == 240 && Math.Abs(a.Fps - 25) < .1 && a.ACodec == "aac" && Math.Abs(a.Duration - 10) < .3, a.Summary);
Check("Probe S: sin audio", !s.HasAudio);
Check("Probe D: 640x360 30fps", d.Width == 640 && Math.Abs(d.Fps - 30) < .1, d.Summary);
bool threw = false; try { await tools.ProbeAsync(P("noexiste.mp4")); } catch (InvalidOperationException) { threw = true; }
Check("Probe de archivo inexistente da error claro", threw);

// 2) Lógica de recorte de inicio por grupos
var cl = new List<Clip> { new(a), new(d), new(await tools.ProbeAsync(P("B.mp4"))) };
cl[0].Mark = true; cl[1].Mark = false; cl[2].Mark = true;
var r1 = ClipOps.TrimStart(cl.Where(c => c.Mark), 3);           // grupo marcado: 3 s
var r2 = ClipOps.TrimStart(cl.Where(c => !c.Mark), 2);          // grupo sin marcar: 2 s
Check("Grupos: marcados 3 s / no marcados 2 s", cl.Select(c => c.Start).SequenceEqual(new double[] { 3, 2, 3 }) && r1.Applied == 2 && r2.Applied == 1);
var r3 = ClipOps.TrimStart(cl, 7);                              // D dura 6 s: debe omitirse
Check("Se omiten los vídeos más cortos que el recorte", r3.Skipped.SequenceEqual(new[] { "D.mp4" }) && cl[1].Start == 2, string.Join(",", r3.Skipped));
Check("Etiqueta de recorte", cl[0].Range.StartsWith("✂ −7 s"), cl[0].Range);

// 3) Exportar copiando
double Dur(string f) => double.Parse(Sh("ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", f).Trim(), CultureInfo.InvariantCulture);
var ex = new Exporter(tools);
var copyClips = new List<Clip> { new(a) { Start = 2, End = 10 }, new(await tools.ProbeAsync(P("B.mp4"))) { Start = 1, End = 7 }, new(a) { Start = 0, End = 6 } };
var phases = new List<ExportProgress>(); var prog = new Progress<ExportProgress>(p => phases.Add(p));
await ex.RunAsync(copyClips, P("copy.mp4"), ExportMode.Copy, VideoEncoder.Cpu, 23, prog, CancellationToken.None);
await Task.Delay(200);
Check("Exportar copiando: duración ≈ 20 s", Math.Abs(Dur(P("copy.mp4")) - 20.3) < 1.0, $"{Dur(P("copy.mp4")):0.00} s");
Check("Se informó el progreso de cada vídeo", phases.Any(p => p.ClipIndex == 2) && phases.Any(p => p.Joining), $"{phases.Count} avisos");
Check("No quedan archivos temporales", !File.Exists(P("copy.mp4.tmp")) && !Directory.GetDirectories(Path.GetTempPath(), "VideoCutter_*").Any());

// 4) Exportar recodificando, con vídeos distintos y uno sin audio
var mix = new List<Clip> { new(a) { Start = 2, End = 10 }, new(d) { Start = 0, End = 6 }, new(s) { Start = 0, End = 5 }, new(await tools.ProbeAsync(P("B.mp4"))) { Start = 1, End = 7 } };
await ex.RunAsync(mix, P("enc.mp4"), ExportMode.Reencode, VideoEncoder.Cpu, 23, null, CancellationToken.None);
var e = await tools.ProbeAsync(P("enc.mp4"));
Check("Recodificar: resolución y fps del primero (320x240 @25)", e.Width == 320 && e.Height == 240 && Math.Abs(e.Fps - 25) < .1, e.Summary);
Check("Recodificar: audio presente (incluido el vídeo mudo) y duración ≈ 25 s", e.HasAudio && Math.Abs(e.Duration - 25) < 0.7, $"{e.Duration:0.00} s");
var pk = Sh("ffprobe", "-v", "error", "-select_streams", "v", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", P("enc.mp4"))
    .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(',')).Select(t => (pts: double.Parse(t[0], CultureInfo.InvariantCulture), key: t[1].Contains('K'))).OrderBy(x => x.pts).ToList();
var keys = pk.Where(x => x.key).Select(x => x.pts).ToList();
double maxKey = keys.Zip(keys.Skip(1), (p, q) => q - p).Max(), maxGap = pk.Zip(pk.Skip(1), (p, q) => q.pts - p.pts).Max();
Check("Fotogramas clave cada ≤ 2 s", maxKey <= 2.05, $"máx {maxKey:0.00} s");
Check("Sin saltos entre fotogramas (≤ 0,05 s)", maxGap <= 0.05, $"máx {maxGap:0.000} s");

// 5) Conversor
var cv = new Converter(tools);
var how1 = await cv.ToMp4Async(await tools.ProbeAsync(P("X.avi")), P("X.mp4"), VideoEncoder.Cpu, 23, null, CancellationToken.None);
var x = await tools.ProbeAsync(P("X.mp4"));
Check("AVI (mpeg4+mp2) → recodifica a H.264 + AAC", how1 == "recodificado" && x.VCodec == "h264" && x.ACodec == "aac", $"{how1}: {x.Summary}");
var how2 = await cv.ToMp4Async(await tools.ProbeAsync(P("H.mkv")), P("H.mp4"), VideoEncoder.Cpu, 23, null, CancellationToken.None);
Check("MKV (h264+aac) → solo copia", how2 == "copiado" && (await tools.ProbeAsync(P("H.mp4"))).VCodec == "h264", how2);

// 6) Cancelar
Gen(P("L.mp4"), "-f", "lavfi", "-i", "testsrc=d=300:s=1280x720:r=25", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p");
var big = new List<Clip> { new(await tools.ProbeAsync(P("L.mp4"))) };
using var cts = new CancellationTokenSource(); var t0 = Stopwatch.StartNew();
var task = ex.RunAsync(big, P("cancel.mp4"), ExportMode.Reencode, VideoEncoder.Cpu, 18, null, cts.Token);
await Task.Delay(1200); cts.Cancel();
bool cancelled = false; try { await task; } catch (OperationCanceledException) { cancelled = true; }
Check("Cancelar detiene la exportación y no deja archivos", cancelled && t0.Elapsed.TotalSeconds < 10 && !File.Exists(P("cancel.mp4")) && !File.Exists(P("cancel.mp4.tmp")) && !Directory.GetDirectories(Path.GetTempPath(), "VideoCutter_*").Any(), $"{t0.Elapsed.TotalSeconds:0.0} s");

// 7) Configuración regional con coma decimal (España)
try
{
    CultureInfo.CurrentCulture = new CultureInfo("es-ES");
    var ci = new List<Clip> { new(a) { Start = 1.5, End = 4.5 } };
    await ex.RunAsync(ci, P("es.mp4"), ExportMode.Copy, VideoEncoder.Cpu, 23, null, CancellationToken.None);
    Check("Cultura es-ES (coma decimal): tiempos con decimales bien", Math.Abs(Dur(P("es.mp4")) - 3.1) < 0.8, $"{Dur(P("es.mp4")):0.00} s");
}
catch (CultureNotFoundException) { Console.WriteLine("  (cultura es-ES no disponible en este sistema; prueba omitida)"); }

Console.WriteLine($"\n{pass} correctas, {fail} fallidas");
return fail == 0 ? 0 : 1;
