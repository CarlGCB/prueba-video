using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace VideoCutter.Core;

/// <summary>Datos técnicos de un vídeo, leídos con ffprobe.</summary>
public sealed record MediaInfo(string Path, double Duration, string? VCodec, int Width, int Height,
                               double Fps, string? ACodec, int SampleRate, long Size)
{
    public bool HasAudio => ACodec != null;
    public string Summary => $"{VCodec ?? "?"} {Width}x{Height} {Fps.ToString("0.##", CultureInfo.InvariantCulture)} fps · {ACodec ?? "sin audio"}";
    /// <summary>Dos vídeos con la misma firma se pueden unir sin recodificar.</summary>
    public string Signature => $"{VCodec}|{Width}|{Height}|{Fps.ToString("0.##", CultureInfo.InvariantCulture)}|{ACodec}|{SampleRate}";
}

/// <summary>Una "diapositiva" del editor: un vídeo con su tramo de inicio y fin.</summary>
public class Clip : INotifyPropertyChanged
{
    public MediaInfo Info { get; }
    public string Name => System.IO.Path.GetFileName(Info.Path);

    double _start, _end;
    bool _mark;
    string? _thumb;
    int _number;

    public Clip(MediaInfo info) { Info = info; _end = Math.Round(info.Duration, 2); }

    public double Start { get => _start; set { if (Set(ref _start, Math.Max(0, value))) { Raise(nameof(Range)); Raise(nameof(Length)); } } }
    public double End   { get => _end;   set { if (Set(ref _end, Math.Max(0, value)))   { Raise(nameof(Range)); Raise(nameof(Length)); } } }
    public bool Mark    { get => _mark;  set => Set(ref _mark, value); }
    public string? Thumb { get => _thumb; set => Set(ref _thumb, value); }
    public int Number   { get => _number; set => Set(ref _number, value); }

    public double Length => Math.Max(0, End - Start);
    public string Range => (Start > 0 ? $"✂ −{Start.ToString("0.#", CultureInfo.InvariantCulture)} s · " : "") + $"{ClipOps.Clock(Start)} → {ClipOps.Clock(End)}";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name!); return true;
    }
}

public static class ClipOps
{
    /// <summary>Fija el inicio en <paramref name="seconds"/> en cada vídeo de la lista. Omite los que duran menos.</summary>
    public static (int Applied, List<string> Skipped) TrimStart(IEnumerable<Clip> clips, double seconds)
    {
        int ok = 0; var skipped = new List<string>();
        foreach (var c in clips)
        {
            if (c.End > 0 && seconds >= c.End) { skipped.Add(c.Name); continue; }
            c.Start = seconds; ok++;
        }
        return (ok, skipped);
    }

    public static string Clock(double seconds)
    {
        var s = (long)Math.Max(0, Math.Round(seconds));
        return $"{s / 3600:00}:{s % 3600 / 60:00}:{s % 60:00}";
    }

    public static void Renumber(IList<Clip> clips) { for (int i = 0; i < clips.Count; i++) clips[i].Number = i + 1; }
}
