using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VideoCutter.Core;

namespace VideoCutter.Desktop;

public partial class MainWindow : Window
{
    const string FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
    static readonly string[] VideoExts = { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".mpg", ".mpeg", ".m4v", ".webm", ".3gp", ".ts", ".mts", ".m2ts", ".vob", ".ogv" };

    static string Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    static string ToolsDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoCutter", "ffmpeg");
    static string ThumbDir => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VideoCutter_thumbs");

    FfmpegTools? tools;
    HashSet<VideoEncoder> encoders = new() { VideoEncoder.Cpu };
    readonly ObservableCollection<QueueItem> queue = new();
    readonly ObservableCollection<Mp4Item> mp4s = new();
    readonly ObservableCollection<Clip> clips = new();
    bool queueRunning;

    // Vista previa
    Clip? playerFor;
    double? clipEnd;
    bool syncing, updatingSeek, playing;
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(150) };

    // Exportación
    CancellationTokenSource? exportCts;
    ExportProgress? lastProgress;
    DateTime lastAt, exportStart;
    string? lastOutput;
    readonly DispatcherTimer etaTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Arrastrar diapositivas
    Point dragStart;
    Clip? dragClip;

    public MainWindow()
    {
        InitializeComponent();
        QueueList.ItemsSource = queue;
        Mp4List.ItemsSource = mp4s;
        SlideList.ItemsSource = clips;
        Directory.CreateDirectory(ThumbDir);
        ticker.Tick += (s, e) => PlayerTick();
        ticker.Start();
        etaTimer.Tick += (s, e) => EtaTick();
        Loaded += async (s, e) => await SetupToolsAsync();
    }

    // ------------------------------------------------------------ FFmpeg
    async Task SetupToolsAsync()
    {
        var exeDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        tools = FfmpegTools.Locate(exeDir, ToolsDir);
        FfmpegBanner.Visibility = tools == null ? Visibility.Visible : Visibility.Collapsed;
        if (tools == null) return;
        encoders = await tools.ListEncodersAsync();
        CmbEncoder.Items.Clear();
        void Add(string text, VideoEncoder v) { if (encoders.Contains(v)) CmbEncoder.Items.Add(new ComboBoxItem { Content = text, Tag = v }); }
        Add("CPU (x264)", VideoEncoder.Cpu); Add("NVIDIA (NVENC)", VideoEncoder.Nvenc);
        Add("Intel (QuickSync)", VideoEncoder.Qsv); Add("AMD (AMF)", VideoEncoder.Amf);
        CmbEncoder.SelectedIndex = 0;
    }

    async void BtnDownloadFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        BtnDownloadFfmpeg.IsEnabled = false; FfmpegProgress.Visibility = Visibility.Visible;
        var zip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ffmpeg-win64.zip");
        try
        {
            Directory.CreateDirectory(ToolsDir);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(40) })
            using (var resp = await http.GetAsync(FfmpegUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1, read = 0;
                using var src = await resp.Content.ReadAsStreamAsync();
                using var dst = File.Create(zip);
                var buf = new byte[81920]; int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n)); read += n;
                    if (total > 0) FfmpegProgress.Value = 100.0 * read / total;
                    FfmpegText.Text = $"Descargando FFmpeg… {read / 1048576} de {(total > 0 ? total / 1048576 : 0)} MB";
                }
            }
            FfmpegText.Text = "Descomprimiendo…";
            await Task.Run(() =>
            {
                using var za = ZipFile.OpenRead(zip);
                foreach (var entry in za.Entries)
                    if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                        entry.ExtractToFile(System.IO.Path.Combine(ToolsDir, entry.Name), true);
            });
            try { File.Delete(zip); } catch { }
            await SetupToolsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo descargar FFmpeg: " + ex.Message + "\n\nAlternativa: instálalo con «winget install Gyan.FFmpeg» y reinicia el programa.", "VideoCutter", MessageBoxButton.OK, MessageBoxImage.Warning);
            BtnDownloadFfmpeg.IsEnabled = true; FfmpegProgress.Visibility = Visibility.Collapsed;
            FfmpegText.Text = "Falta FFmpeg, el motor que corta y une los vídeos. Se descarga una sola vez (unos 200 MB).";
        }
    }

    // ------------------------------------------------------------ Paso 1: añadir y convertir
    void DropZone_DragOver(object sender, DragEventArgs e) { e.Effects = DragDropEffects.Copy; e.Handled = true; }

    void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files) HandleFiles(files);
    }

    void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "Vídeos|" + string.Join(";", VideoExts.Select(x => "*" + x)) + "|Todos los archivos|*.*" };
        if (dlg.ShowDialog(this) == true) HandleFiles(dlg.FileNames);
    }

    static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) { foreach (var f in Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) yield return f; }
            else yield return p;
        }
    }

    async void HandleFiles(IEnumerable<string> paths)
    {
        if (tools == null) { MessageBox.Show("Primero hay que descargar FFmpeg (aviso amarillo de arriba).", "VideoCutter"); return; }
        foreach (var p in Expand(paths).ToList())
        {
            var ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
            if (!VideoExts.Contains(ext)) continue;
            if (ext == ".mp4")
            {
                try { mp4s.Add(new Mp4Item(await tools.ProbeAsync(p))); }
                catch (Exception ex) { CStat.Text = "❌ " + ex.Message; }
            }
            else queue.Add(new QueueItem(p));
        }
        BtnToEditor.IsEnabled = mp4s.Count > 0;
        _ = ProcessQueueAsync();
    }

    static string UniquePath(string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var name = System.IO.Path.GetFileNameWithoutExtension(file); var ext = System.IO.Path.GetExtension(file);
        var path = System.IO.Path.Combine(dir, file); int i = 2;
        while (File.Exists(path)) path = System.IO.Path.Combine(dir, $"{name} ({i++}){ext}");
        return path;
    }

    VideoEncoder SelectedEncoder => (CmbEncoder.SelectedItem as ComboBoxItem)?.Tag is VideoEncoder v ? v : VideoEncoder.Cpu;
    int Crf => (int)Math.Clamp(ParseD(TxtCrf.Text, 22), 12, 35);
    static double ParseD(string? s, double fallback = 0)
        => double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    async Task ProcessQueueAsync()
    {
        if (queueRunning || tools == null) return;
        queueRunning = true;
        try
        {
            QueueItem? q;
            while ((q = queue.FirstOrDefault(x => x.State == QState.Waiting)) != null)
            {
                q.State = QState.Working; q.Status = "Analizando…";
                try
                {
                    var info = await tools.ProbeAsync(q.FilePath);
                    var outPath = UniquePath(System.IO.Path.Combine(Videos, "VideoCutter", "Convertidos"), System.IO.Path.GetFileNameWithoutExtension(q.FilePath) + ".mp4");
                    var item = q;
                    var how = await new Converter(tools).ToMp4Async(info, outPath, SelectedEncoder, Crf,
                        (sec, sp) => Dispatcher.BeginInvoke(() => item.Status = $"Convirtiendo… {Math.Min(99, (int)(100 * sec / Math.Max(1, info.Duration)))} %"),
                        CancellationToken.None);
                    var outInfo = await tools.ProbeAsync(outPath);
                    queue.Remove(q); mp4s.Add(new Mp4Item(outInfo));
                }
                catch (Exception ex) { q.State = QState.Error; q.Status = "❌ " + ex.Message; }
                BtnToEditor.IsEnabled = mp4s.Count > 0;
            }
        }
        finally { queueRunning = false; }
    }

    void RemoveMp4_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is Mp4Item m) mp4s.Remove(m);
        BtnToEditor.IsEnabled = mp4s.Count > 0;
    }

    // ------------------------------------------------------------ Paso 2: editor
    void BtnToEditor_Click(object sender, RoutedEventArgs e)
    {
        foreach (var m in mp4s.ToList()) { var c = new Clip(m.Info) { Mark = true }; clips.Add(c); _ = MakeThumbAsync(c); }
        mp4s.Clear(); BtnToEditor.IsEnabled = false;
        ClipOps.Renumber(clips);
        EditorPanel.Visibility = Visibility.Visible;
        if (SlideList.SelectedItem == null && clips.Count > 0) SlideList.SelectedIndex = 0;
        BtnExport.IsEnabled = clips.Count > 0 && exportCts == null;
    }

    async Task MakeThumbAsync(Clip c)
    {
        if (tools == null) return;
        var jpg = System.IO.Path.Combine(ThumbDir, Guid.NewGuid().ToString("N") + ".jpg");
        if (await tools.ThumbnailAsync(c.Info.Path, jpg, Math.Min(1, c.Info.Duration / 2))) c.Thumb = jpg;
    }

    Clip? Cur => SlideList.SelectedItem as Clip;

    void SlideList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail();

    void ShowDetail()
    {
        var c = Cur;
        DetailPanel.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
        if (c == null) return;
        DName.Text = $"{c.Number}. {c.Name}";
        DInfo.Text = c.Info.Summary + (clips.Count > 0 && c.Info.Signature != clips[0].Info.Signature ? "  ⚠️ distinto al vídeo 1" : "");
        SetTimeBoxes(c);
        if (!ReferenceEquals(playerFor, c))
        {
            playerFor = c; clipEnd = null; PreviewNote.Text = "";
            Player.Source = new Uri(c.Info.Path);
            Player.Position = TimeSpan.Zero; Player.Play(); Player.Pause(); playing = false;
        }
    }

    void SetTimeBoxes(Clip c)
    {
        syncing = true;
        TxtStart.Text = c.Start.ToString("0.##", CultureInfo.InvariantCulture);
        TxtEnd.Text = c.End.ToString("0.##", CultureInfo.InvariantCulture);
        syncing = false;
        DDur.Text = "Duración del recorte: " + ClipOps.Clock(c.Length);
    }

    void TxtTimes_TextChanged(object sender, TextChangedEventArgs e)
    {
        var c = Cur; if (syncing || c == null) return;
        c.Start = ParseD(TxtStart.Text, c.Start); c.End = ParseD(TxtEnd.Text, c.End);
        DDur.Text = "Duración del recorte: " + ClipOps.Clock(c.Length);
    }

    void BtnSetStart_Click(object sender, RoutedEventArgs e) { var c = Cur; if (c == null) return; c.Start = Math.Round(Player.Position.TotalSeconds, 2); SetTimeBoxes(c); }
    void BtnSetEnd_Click(object sender, RoutedEventArgs e) { var c = Cur; if (c == null) return; c.End = Math.Round(Player.Position.TotalSeconds, 2); SetTimeBoxes(c); }

    void BtnPlayClip_Click(object sender, RoutedEventArgs e)
    {
        var c = Cur; if (c == null) return;
        Player.Position = TimeSpan.FromSeconds(c.Start); clipEnd = c.End; Player.Play(); playing = true;
    }

    void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (playing) { Player.Pause(); playing = false; } else { clipEnd = null; Player.Play(); playing = true; }
    }

    void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan) { updatingSeek = true; Seek.Maximum = Math.Max(1, Player.NaturalDuration.TimeSpan.TotalSeconds); updatingSeek = false; }
    }

    void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        => PreviewNote.Text = "Sin vista previa: Windows no puede reproducir este vídeo. Se puede editar igualmente escribiendo los tiempos.";

    void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingSeek) return;
        Player.Position = TimeSpan.FromSeconds(Seek.Value);
    }

    void PlayerTick()
    {
        if (Cur == null || Player.Source == null) return;
        var pos = Player.Position.TotalSeconds;
        if (clipEnd.HasValue && playing && pos >= clipEnd.Value) { Player.Pause(); playing = false; clipEnd = null; }
        updatingSeek = true; if (pos <= Seek.Maximum) Seek.Value = pos; updatingSeek = false;
        var dur = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        PosText.Text = $"{ClipOps.Clock(pos)} / {ClipOps.Clock(dur)}";
    }

    void Move(int d)
    {
        var c = Cur; if (c == null) return;
        int i = clips.IndexOf(c), j = i + d; if (j < 0 || j >= clips.Count) return;
        clips.Move(i, j); ClipOps.Renumber(clips); SlideList.SelectedItem = c; ShowDetail();
    }
    void BtnLeft_Click(object sender, RoutedEventArgs e) => Move(-1);
    void BtnRight_Click(object sender, RoutedEventArgs e) => Move(1);

    void BtnRemoveClip_Click(object sender, RoutedEventArgs e)
    {
        var c = Cur; if (c == null) return;
        int i = clips.IndexOf(c); clips.Remove(c); ClipOps.Renumber(clips);
        if (clips.Count > 0) SlideList.SelectedIndex = Math.Min(i, clips.Count - 1);
        else { Player.Stop(); Player.Source = null; playerFor = null; DetailPanel.Visibility = Visibility.Collapsed; }
        BtnExport.IsEnabled = clips.Count > 0 && exportCts == null;
    }

    // ---- Arrastrar diapositivas para ordenarlas
    static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    void Slide_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<CheckBox>(e.OriginalSource as DependencyObject) != null) { dragClip = null; return; }
        dragStart = e.GetPosition(null);
        dragClip = (sender as ListBoxItem)?.DataContext as Clip;
    }

    void Slide_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragClip == null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pos.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var c = dragClip; dragClip = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject("clip", c), DragDropEffects.Move);
    }

    void Slide_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("clip")) return;
        var moved = e.Data.GetData("clip") as Clip; var target = (sender as ListBoxItem)?.DataContext as Clip;
        if (moved == null || target == null || ReferenceEquals(moved, target)) return;
        clips.Move(clips.IndexOf(moved), clips.IndexOf(target)); ClipOps.Renumber(clips);
        SlideList.SelectedItem = moved; ShowDetail(); e.Handled = true;
    }

    // ---- Quitar el principio
    double TrimSeconds => ParseD(TxtMin.Text) * 60 + ParseD(TxtSec.Text);

    void ApplyTrim(List<Clip> list)
    {
        if (list.Count == 0) { TrimMsg.Text = "No hay vídeos en ese grupo."; return; }
        var (ok, skipped) = ClipOps.TrimStart(list, TrimSeconds);
        TrimMsg.Text = $"Inicio fijado en {ClipOps.Clock(TrimSeconds)} en {ok} vídeo(s)." + (skipped.Count > 0 ? $" Omitidos (más cortos): {string.Join(", ", skipped)}." : "")
                       + " Con copia sin recodificar, el corte real puede caer unos segundos antes.";
        if (Cur != null) SetTimeBoxes(Cur);
    }
    void BtnTrimApply_Click(object sender, RoutedEventArgs e)
        => ApplyTrim(CmbScope.SelectedIndex switch { 1 => clips.Where(c => !c.Mark).ToList(), 2 => clips.ToList(), _ => clips.Where(c => c.Mark).ToList() });
    void BtnTrimOne_Click(object sender, RoutedEventArgs e) { if (Cur != null) ApplyTrim(new List<Clip> { Cur }); }
    void BtnMarkAll_Click(object sender, RoutedEventArgs e) { foreach (var c in clips) c.Mark = true; }
    void BtnMarkNone_Click(object sender, RoutedEventArgs e) { foreach (var c in clips) c.Mark = false; }
    void BtnMarkInvert_Click(object sender, RoutedEventArgs e) { foreach (var c in clips) c.Mark = !c.Mark; }

    // ------------------------------------------------------------ Paso 3: exportar
    async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (tools == null || clips.Count == 0) return;
        string output;
        if (ChkAutoSave.IsChecked == true)
            output = UniquePath(System.IO.Path.Combine(Videos, "VideoCutter"), $"final {DateTime.Now:yyyy-MM-dd HH-mm}.mp4");
        else
        {
            var dlg = new SaveFileDialog { Filter = "Vídeo MP4|*.mp4", FileName = "final.mp4", InitialDirectory = Videos };
            if (dlg.ShowDialog(this) != true) return;
            output = dlg.FileName;
        }
        var mode = CmbMode.SelectedIndex == 1 ? ExportMode.Copy : ExportMode.Reencode;
        var enc = SelectedEncoder; int crf = Crf;
        var list = clips.ToList();

        exportCts = new CancellationTokenSource(); lastProgress = null; lastOutput = null;
        BtnExport.IsEnabled = false; BtnCancel.Visibility = Visibility.Visible; BtnOpenResult.Visibility = Visibility.Collapsed;
        EtaPanel.Visibility = Visibility.Visible; EtaLabel.Text = "Tiempo restante"; EtaClock.Text = "Calculando…"; EtaBar.Value = 0; EtaEnd.Text = ""; EtaLine.Text = "";
        EtaPhase.Text = "Preparando…";
        exportStart = DateTime.UtcNow; etaTimer.Start();
        using var sleep = new SleepGuard();
        try
        {
            await new Exporter(tools).RunAsync(list, output, mode, enc, crf, new Progress<ExportProgress>(OnProgress), exportCts.Token);
            var total = (DateTime.UtcNow - exportStart).TotalSeconds;
            lastOutput = output;
            EtaPhase.Text = "✅ Exportación completada"; EtaLabel.Text = "Tiempo total"; EtaClock.Text = ClipOps.Clock(total); EtaBar.Value = 100; EtaEnd.Text = "";
            EtaLine.Text = $"{ClipOps.Clock(list.Sum(c => c.Length))} de vídeo · {new FileInfo(output).Length / 1048576} MB · {output}";
            BtnOpenResult.Visibility = Visibility.Visible;
            if (ChkOpenFolder.IsChecked == true) BtnOpenResult_Click(this, new RoutedEventArgs());
        }
        catch (OperationCanceledException) { EtaPhase.Text = "Exportación cancelada."; EtaClock.Text = "--:--:--"; }
        catch (Exception ex) { EtaPhase.Text = "❌ " + ex.Message; EtaClock.Text = "--:--:--"; MessageBox.Show(ex.Message, "VideoCutter · no se pudo exportar", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            etaTimer.Stop(); BtnCancel.Visibility = Visibility.Collapsed;
            exportCts?.Dispose(); exportCts = null; BtnExport.IsEnabled = clips.Count > 0;
        }
    }

    void BtnCancel_Click(object sender, RoutedEventArgs e) { exportCts?.Cancel(); }

    void BtnOpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (lastOutput != null && File.Exists(lastOutput)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{lastOutput}\"") { UseShellExecute = true });
    }

    void OnProgress(ExportProgress p)
    {
        lastProgress = p; lastAt = DateTime.UtcNow;
        EtaPhase.Text = p.Phase;
        EtaBar.Value = Math.Min(100, 100 * p.ProcessedSec / Math.Max(1, p.TotalSec));
        EtaTick();
    }

    // Cuenta atrás: se actualiza cada segundo entre aviso y aviso de FFmpeg.
    void EtaTick()
    {
        var p = lastProgress; if (p == null || exportCts == null) return;
        double el = (DateTime.UtcNow - exportStart).TotalSeconds;
        double? rem = null;
        if (!p.Joining && p.EtaSec is double eta) rem = Math.Max(0, eta - (DateTime.UtcNow - lastAt).TotalSeconds);
        EtaClock.Text = p.Joining ? "Casi listo…" : rem == null ? "Calculando…" : ClipOps.Clock(rem.Value);
        EtaLine.Text = $"Procesado {ClipOps.Clock(p.ProcessedSec)} de {ClipOps.Clock(p.TotalSec)} ({Math.Min(100, (int)(100 * p.ProcessedSec / Math.Max(1, p.TotalSec)))} %) · Transcurrido {ClipOps.Clock(el)}"
                       + (p.Speed > 0 ? $" · velocidad ×{p.Speed:0.00}" : "");
        EtaEnd.Text = rem == null ? "" : $"Termina hacia las {DateTime.Now.AddSeconds(rem.Value):HH:mm} (estimación)";
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (exportCts != null && MessageBox.Show("Hay una exportación en marcha. Si cierras, se pierde. ¿Cerrar de todos modos?", "VideoCutter", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        exportCts?.Cancel();
        try { Directory.Delete(ThumbDir, true); } catch { }
    }
}
