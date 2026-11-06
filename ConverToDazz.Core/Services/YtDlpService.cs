using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ConverToDazz.Core.Services
{
    public class DownloadItem
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Status { get; set; } = "";
        public int Progress { get; set; }
        public DateTime AddedTime { get; set; } = DateTime.Now;
        public long FileSize { get; set; }
        public string Format { get; set; } = "";
        public string Resolution { get; set; } = "";
        public string ConsoleLog { get; set; } = "";

        /// <summary>Что реально отдал сайт и что получилось в файле (кодек, битрейт, «ненастоящий» lossless и т.п.).</summary>
        public string SourceInfo { get; set; } = "";
    }

    public class VideoFormat
    {
        public string FormatId { get; set; } = "";
        public string Extension { get; set; } = "";
        public string Resolution { get; set; } = "";
        public string Vcodec { get; set; } = "";
        public string Acodec { get; set; } = "";
        public long? Filesize { get; set; }
        public string FormatNote { get; set; } = "";
        public double? Fps { get; set; }
        public int? Height { get; set; }
        public string? FFmpegPath { get; set; }

        public override string ToString()
        {
            var info = $"{Resolution}";
            if (!string.IsNullOrEmpty(Vcodec) && Vcodec != "none")
                info += $" - {Vcodec}";
            if (Fps.HasValue && Fps > 0)
                info += $" {Fps}fps";
            if (!string.IsNullOrEmpty(FormatNote))
                info += $" ({FormatNote})";
            if (Filesize.HasValue && Filesize > 0)
                info += $" - {FormatFileSize(Filesize.Value)}";
            return info;
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double len = bytes;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    public class YtDlpService
    {
        // ───────── регулярки (раньше Regex.Match с литералом на каждую строку вывода) ─────────
        private static readonly Regex ProgressRegex = new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
        private static readonly Regex ItemHeaderRegex = new(@"\[download\]\s+Downloading item (\d+) of (\d+)", RegexOptions.Compiled);
        private static readonly Regex DestinationRegex = new(@"\[download\]\s+Destination:\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex FormatsCountRegex = new(@"Downloading (\d+) format\(s\):\s*(\S+)", RegexOptions.Compiled);

        private static readonly string[] PathPatterns =
        {
            @"\[download\]\s+Destination:\s+(.+)$",
            @"\[Merger\]\s+Merging formats into\s+""(.+)""",
            @"\[Merger\]\s+Merging formats into\s+(.+)$",
            @"\[ExtractAudio\]\s+Destination:\s+(.+)$",
            @"\[ffmpeg\]\s+Destination:\s+(.+)$",
            @"\[ExtractAudio\]\s+""(.+)""",
            @"\[Merger\]\s+""(.+)"""
        };
        private static readonly Regex[] PathRegexes = PathPatterns.Select(p => new Regex(p, RegexOptions.Compiled)).ToArray();

        private const int MaxLogChars = 60_000;

        private string _downloadPath;
        private readonly List<DownloadItem> _downloadHistory;
        private readonly object _historyLock = new();
        private string? _proxyUrl;
        public string? FFmpegPath { get; set; }

        private static YtDlpToolsService Tools => YtDlpToolsService.Instance;

        /// <summary>ffmpeg: сначала обновляемая копия (AppData), затем найденная при старте (Resources\ffmpeg).</summary>
        private string? ResolveFfmpeg()
        {
            var managed = Tools.ManagedFfmpegPath;
            if (File.Exists(managed)) return managed;
            return !string.IsNullOrWhiteSpace(FFmpegPath) && File.Exists(FFmpegPath) ? FFmpegPath : null;
        }

        public YtDlpService()
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;

            var ffmpegPaths = new[]
            {
                Path.Combine(basePath, "Resources", "ffmpeg", "ffmpeg.exe"),
                Path.Combine(basePath, "Resources", "Tools", "ffmpeg.exe"),
                Path.Combine(basePath, "ffmpeg.exe"),
                Path.Combine(basePath, "Tools", "ffmpeg.exe")
            };
            foreach (var p in ffmpegPaths)
            {
                if (File.Exists(p)) { FFmpegPath = p; break; }
            }

            _downloadPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "DazzConv_Downloads");

            if (!Directory.Exists(_downloadPath))
                Directory.CreateDirectory(_downloadPath);

            _downloadHistory = new List<DownloadItem>();
            LoadDownloadHistory();
        }

        private Process? _currentProcess;
        private readonly object _processLock = new object();

        public void SetProxy(string? proxyUrl) => _proxyUrl = proxyUrl;

        /// <summary>
        /// yt-dlp.exe берётся у YtDlpToolsService при КАЖДОМ запуске: после обновления из настроек
        /// сразу используется новая версия (раньше путь определялся один раз в конструкторе).
        /// </summary>
        private string YtDlpPath
        {
            get
            {
                var p = Tools.YtDlpPath;
                return File.Exists(p) ? p : "yt-dlp";   // как и раньше: запасной вариант — из PATH
            }
        }

        private ProcessStartInfo CreatePsi(IEnumerable<string> args, bool redirectOut = true, bool redirectErr = true, string? workDir = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = YtDlpPath,
                RedirectStandardOutput = redirectOut,
                RedirectStandardError = redirectErr,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = redirectOut ? Encoding.UTF8 : null,
                StandardErrorEncoding = redirectErr ? Encoding.UTF8 : null
            };
            if (workDir != null) psi.WorkingDirectory = workDir;

            // ArgumentList: никакой ручной сборки кавычек — ссылка с кавычкой/пробелом не может «вылезти» в другие опции
            foreach (var a in args) psi.ArgumentList.Add(a);
            return psi;
        }

        /// <summary>Общие опции для всех запросов: плагин PO-токенов (если установлен) и прокси.</summary>
        private List<string> CommonArgs()
        {
            var args = new List<string>();
            if (Tools.PotInstalled)
            {
                args.AddRange(new[] { "--plugin-dirs", "default", "--plugin-dirs", Tools.PluginsDir });
            }
            if (!string.IsNullOrWhiteSpace(_proxyUrl))
                args.AddRange(new[] { "--proxy", _proxyUrl });
            return args;
        }

        private static bool IsYouTube(string url) =>
            url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

        private async Task PrepareForUrlAsync(string url)
        {
            // для YouTube поднимаем локальный сервер PO-токенов, если он установлен
            if (IsYouTube(url) && Tools.PotInstalled)
            {
                try { await Tools.EnsurePotServerAsync(); } catch { }
            }
        }

        /// <summary>Читает stdout/stderr, не давая процессу зависнуть на заполненном буфере, и убивает его при отмене.</summary>
        private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCaptureAsync(ProcessStartInfo psi, CancellationToken token)
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            using var reg = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = psi.RedirectStandardError ? process.StandardError.ReadToEndAsync() : Task.FromResult("");

            await process.WaitForExitAsync(token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (process.ExitCode, stdout, stderr);
        }

        public async Task<bool> TestConnectionAsync(string testUrl, CancellationToken token = default)
        {
            try
            {
                var args = new List<string> { "--simulate", "--no-download", "--socket-timeout", "10" };
                args.AddRange(CommonArgs());
                args.Add("--");
                args.Add(testUrl);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                // раньше stdout/stderr не читались (буфер мог заполниться), а по таймауту процесс оставался жить
                var (exit, _, _) = await RunCaptureAsync(CreatePsi(args), timeoutCts.Token);
                return exit == 0;
            }
            catch { return false; }
        }

        public string DownloadPath
        {
            get => _downloadPath;
            set
            {
                _downloadPath = value;
                if (!Directory.Exists(_downloadPath)) Directory.CreateDirectory(_downloadPath);
            }
        }

        public void SetDownloadPath(string path) => DownloadPath = path;

        // Снимок под lock: фоновые потоки yt-dlp добавляют записи, а страница в это время перечисляет список
        public IReadOnlyList<DownloadItem> DownloadHistory
        {
            get { lock (_historyLock) return _downloadHistory.ToList(); }
        }

        // ───────── JSON-хелперы: filesize/fps/height часто null — GetInt64()/GetDouble() на null падали ─────────
        private static string JsonString(JsonElement e, string name, string fallback = "") =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

        private static long? JsonLong(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

        private static double? JsonDouble(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;

        public async Task<List<VideoFormat>> GetAvailableFormatsAsync(string url, CancellationToken token = default)
        {
            var formats = new List<VideoFormat>();

            try
            {
                await PrepareForUrlAsync(url);

                var args = new List<string> { "-j", "--no-playlist" };
                args.AddRange(CommonArgs());
                args.Add("--");
                args.Add(url);

                var (exit, json, _) = await RunCaptureAsync(CreatePsi(args), token);

                if (exit == 0 && !string.IsNullOrEmpty(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("formats", out var formatsArray))
                    {
                        foreach (var f in formatsArray.EnumerateArray())
                        {
                            formats.Add(new VideoFormat
                            {
                                FormatId = JsonString(f, "format_id"),
                                Extension = JsonString(f, "ext"),
                                Resolution = JsonString(f, "resolution", "unknown"),
                                Vcodec = JsonString(f, "vcodec", "none"),
                                Acodec = JsonString(f, "acodec", "none"),
                                Filesize = JsonLong(f, "filesize") ?? JsonLong(f, "filesize_approx"),
                                FormatNote = JsonString(f, "format_note"),
                                Fps = JsonDouble(f, "fps"),
                                Height = JsonLong(f, "height") is long h ? (int)h : null
                            });
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Debug.WriteLine($"Error getting formats: {ex.Message}"); }

            return formats.OrderByDescending(f => f.Height ?? 0).ThenByDescending(f => f.Filesize ?? 0).ToList();
        }

        // ───────── буфер лога: ConsoleLog обновляется не чаще раза в 300 мс и ограничен по размеру ─────────
        private sealed class LogBuffer
        {
            private readonly StringBuilder _sb = new();
            private readonly Stopwatch _sw = Stopwatch.StartNew();
            private readonly DownloadItem _item;
            private int _lastLoggedTenth = -1;
            private long _lastFlush;

            public LogBuffer(DownloadItem item) { _item = item; }

            public void Add(string line)
            {
                // строки прогресса: в лог попадает только каждые ~10% (раньше — сотни строк на файл)
                var m = ProgressRegex.Match(line);
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                {
                    var tenth = (int)(p / 10);
                    if (tenth == _lastLoggedTenth && p < 100) return;
                    _lastLoggedTenth = tenth;
                }

                lock (_sb)
                {
                    _sb.Append(line).Append('\n');
                    if (_sb.Length > MaxLogChars) _sb.Remove(0, _sb.Length - MaxLogChars / 2);

                    if (_sw.ElapsedMilliseconds - _lastFlush > 300)
                    {
                        _item.ConsoleLog = _sb.ToString();
                        _lastFlush = _sw.ElapsedMilliseconds;
                    }
                }
            }

            public void Flush() { lock (_sb) _item.ConsoleLog = _sb.ToString(); }
        }

        private static string StderrPrefix(string line) =>
            line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ? "[WARNING] " :
            line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ? "[ERROR] " : "";

        public async Task<DownloadItem> DownloadAsync(
            string url,
            string? formatId = null,
            bool isAudioOnly = false,
            string? customOutputPath = null,
            IProgress<string>? progress = null,
            CancellationToken token = default,
            string audioFormat = "mp3",
            int audioBitrate = 320,
            string videoContainer = "mkv",
            bool isPlaylist = false,
            DownloadItem? existingItem = null)
        {
            var item = existingItem ?? new DownloadItem();
            item.Url = url;
            item.Status = "Получение информации...";
            item.Progress = 0;
            item.ConsoleLog = "";

            // БАГ: раньше запись добавлялась в историю только при existingItem == null,
            // а страница всегда передаёт свой объект — поэтому новые загрузки в историю не попадали.
            AddToHistory(item);
            SaveDownloadHistory();

            var log = new LogBuffer(item);
            Process? process = null;
            var sourceFormats = new Dictionary<string, SrcFormat>(StringComparer.OrdinalIgnoreCase);
            var chosenFormatId = "";

            try
            {
                await PrepareForUrlAsync(url);

                // ───── название (для имени файла) ─────
                var infoArgs = new List<string> { "-j", "--no-playlist" };
                infoArgs.AddRange(CommonArgs());
                infoArgs.AddRange(JsRuntimeArgs());
                infoArgs.Add("--");
                infoArgs.Add(url);

                var (infoExit, jsonOutput, infoErr) = await RunCaptureAsync(CreatePsi(infoArgs), token);
                // предупреждения/ошибки этапа «получение названия» тоже попадают в лог загрузки
                foreach (var l in infoErr.Split('\n'))
                {
                    var t = l.Trim();
                    if (t.Length > 0) log.Add(StderrPrefix(t) + t);
                }
                if (infoExit == 0 && !string.IsNullOrEmpty(jsonOutput))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(jsonOutput);
                        var title = JsonString(doc.RootElement, "title");
                        if (string.IsNullOrEmpty(title)) title = JsonString(doc.RootElement, "fulltitle");
                        item.Title = string.IsNullOrEmpty(title) ? Path.GetFileName(url) : title;
                        sourceFormats = ParseFormats(doc.RootElement);
                    }
                    catch { item.Title = Path.GetFileName(url); }
                }
                if (string.IsNullOrEmpty(item.Title)) item.Title = Path.GetFileName(url);

                string outputPath;
                if (isPlaylist)
                {
                    outputPath = Path.Combine(_downloadPath, "%(playlist_title)s", "%(playlist_index)03d - %(title)s.%(ext)s");
                }
                else
                {
                    var extension = isAudioOnly ? audioFormat : videoContainer;
                    if (isAudioOnly && audioFormat.Equals("best", StringComparison.OrdinalIgnoreCase))
                    {
                        // расширение заранее неизвестно (opus/m4a/mp3/…): шаблон yt-dlp %(ext)s, имя — уникальное
                        var safe = SanitizeFileName(item.Title);
                        if (string.IsNullOrWhiteSpace(safe)) safe = $"download_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
                        var exists = Directory.Exists(_downloadPath) && Directory.GetFiles(_downloadPath, safe + ".*")
                            .Any(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
                        if (exists) safe += $"_{DateTime.Now:HH_mm_ss_dd_MM_yy}";
                        outputPath = Path.Combine(_downloadPath, safe + ".%(ext)s");
                    }
                    else if (string.IsNullOrEmpty(customOutputPath))
                        outputPath = GetUniqueOutputPath(_downloadPath, item.Title, extension);
                    else
                        outputPath = Directory.Exists(customOutputPath)
                            ? GetUniqueOutputPath(customOutputPath, item.Title, extension)
                            : customOutputPath;
                }

                var filesBeforeDownload = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(_downloadPath))
                {
                    foreach (var f in Directory.GetFiles(_downloadPath))
                    {
                        try { filesBeforeDownload[f] = File.GetLastWriteTimeUtc(f); } catch { }
                    }
                }

                var args = BuildArguments(url, outputPath, formatId, isAudioOnly, isPlaylist, audioFormat, audioBitrate, videoContainer);
                Debug.WriteLine($"[YtDlp] Args: {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

                process = new Process { StartInfo = CreatePsi(args, workDir: _downloadPath), EnableRaisingEvents = true };

                var tail = new Queue<string>();
                var downloadedFilePaths = new List<string>();
                var pathsLock = new object();
                var onlyOneFormat = false;
                var needsPoToken = false;

                void HandleLine(string line, string prefix)
                {
                    log.Add(prefix + line);
                    progress?.Report(line);
                    lock (tail) { tail.Enqueue(line); if (tail.Count > 10) tail.Dequeue(); }

                    var fm = FormatsCountRegex.Match(line);
                    if (fm.Success) chosenFormatId = fm.Groups[2].Value;
                    if (fm.Success && fm.Groups[1].Value == "1" && fm.Groups[2].Value == "18") onlyOneFormat = true;
                    if (line.Contains("PO Token", StringComparison.OrdinalIgnoreCase)) needsPoToken = true;

                    lock (pathsLock) ParseDownloadedFilePath(line, downloadedFilePaths);
                }

                process.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    ParseProgress(e.Data, item);
                    HandleLine(e.Data, "");
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    HandleLine(e.Data, StderrPrefix(e.Data));
                };

                process.Start();
                lock (_processLock) _currentProcess = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                item.Status = "Загрузка...";
                SaveDownloadHistory();

                // БАГ: отмена токеном раньше НЕ убивала yt-dlp (WaitForExitAsync лишь бросает исключение),
                // а CancelDownload() ничего не делал, потому что _currentProcess нигде не присваивался.
                using var reg = token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                });

                await process.WaitForExitAsync(token);
                log.Flush();

                if (process.ExitCode == 0)
                {
                    item.Status = "✅ Завершено";
                    item.Progress = 100;
                    string? downloadedFile = null;

                    lock (pathsLock) { if (downloadedFilePaths.Any()) downloadedFile = downloadedFilePaths.Last(); }

                    if (downloadedFile == null && Directory.Exists(_downloadPath))
                    {
                        await Task.Delay(1000, token);
                        var newOrModifiedFiles = new List<(string path, DateTime time, long size)>();

                        foreach (var file in Directory.GetFiles(_downloadPath))
                        {
                            try
                            {
                                var fi = new FileInfo(file);
                                var lastWrite = fi.LastWriteTimeUtc;
                                bool isNew = !filesBeforeDownload.ContainsKey(file);
                                bool isModified = filesBeforeDownload.TryGetValue(file, out var oldTime) && lastWrite > oldTime;
                                if ((isNew || isModified) && fi.Length > 0)
                                    newOrModifiedFiles.Add((file, lastWrite, fi.Length));
                            }
                            catch { }
                        }
                        if (newOrModifiedFiles.Any())
                            downloadedFile = newOrModifiedFiles.OrderByDescending(f => f.time).ThenByDescending(f => f.size).First().path;
                    }

                    if (!string.IsNullOrEmpty(downloadedFile) && File.Exists(downloadedFile))
                    {
                        await WaitForFileReady(downloadedFile, token);
                        ApplyFileInfo(item, downloadedFile);

                        if (isAudioOnly)
                        {
                            // реальная проверка результата: что отдал сайт и что получилось в файле
                            var audioId = chosenFormatId.Split('+').LastOrDefault() ?? "";
                            sourceFormats.TryGetValue(audioId, out var src);
                            item.SourceInfo = await BuildSourceInfoAsync(downloadedFile, src, audioFormat, audioBitrate, token);
                            log.Add("[SOURCE] " + item.SourceInfo);
                            log.Flush();
                            if (item.SourceInfo.Contains("⚠️")) progress?.Report("⚠️ " + item.SourceInfo);
                        }
                    }
                    else
                    {
                        downloadedFile = TryFindFileByTitle(item.Title, _downloadPath, isAudioOnly, audioFormat);
                        if (!string.IsNullOrEmpty(downloadedFile) && File.Exists(downloadedFile))
                            ApplyFileInfo(item, downloadedFile);
                        else item.Status = "⚠️ Завершено, но файл не найден.";
                    }

                    if (onlyOneFormat && !isAudioOnly && IsYouTube(url))
                    {
                        var hint = needsPoToken || !Tools.PotInstalled
                            ? "⚠️ YouTube отдал только 360p (формат 18): установите поставщика PO-токенов в «Настройках»."
                            : "⚠️ YouTube отдал только 360p (формат 18). Обновите yt-dlp в «Настройках».";
                        item.ConsoleLog += "\n" + hint + "\n";
                        progress?.Report(hint);
                    }
                }
                else
                {
                    string errorMsg;
                    lock (tail) errorMsg = string.Join("\n", tail.TakeLast(3));
                    item.Status = $"❌ Ошибка (код {process.ExitCode})";
                    if (!string.IsNullOrEmpty(errorMsg)) item.Status += $": {errorMsg}";
                }

                SaveDownloadHistory();
                return item;
            }
            catch (OperationCanceledException)
            {
                log.Flush();
                item.Status = "⏹ Отменено";
                SaveDownloadHistory();
                throw;
            }
            catch (Exception ex)
            {
                log.Flush();
                item.Status = $"❌ Ошибка: {ex.Message}";
                item.ConsoleLog += "\n[EXCEPTION] " + ex;
                SaveDownloadHistory();
                throw;
            }
            finally
            {
                lock (_processLock) { if (ReferenceEquals(_currentProcess, process)) _currentProcess = null; }
                process?.Dispose();
            }
        }

        // ───────────────────────── проверка источника звука ─────────────────────────

        private sealed record SrcFormat(string Id, string Acodec, double? Abr, string Ext);

        private static Dictionary<string, SrcFormat> ParseFormats(JsonElement root)
        {
            var d = new Dictionary<string, SrcFormat>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty("formats", out var arr) || arr.ValueKind != JsonValueKind.Array) return d;
            foreach (var f in arr.EnumerateArray())
            {
                var id = JsonString(f, "format_id");
                if (id.Length == 0) continue;
                d[id] = new SrcFormat(id, JsonString(f, "acodec"), JsonDouble(f, "abr") ?? JsonDouble(f, "tbr"), JsonString(f, "ext"));
            }
            return d;
        }

        private static bool IsLosslessSource(SrcFormat src)
        {
            var c = src.Acodec.ToLowerInvariant();
            if (c is "flac" or "alac" or "ape" or "wavpack" or "tta" or "mlp" or "truehd" || c.StartsWith("pcm")) return true;
            if (c.Length == 0 || c == "none" || c == "unknown")
                return src.Ext.ToLowerInvariant() is "flac" or "wav" or "aiff" or "aif" or "alac" or "ape";   // кодек не указан — смотрим расширение
            return false;
        }

        private async Task<string> BuildSourceInfoAsync(string file, SrcFormat? src, string requestedFormat, int requestedBitrate, CancellationToken token)
        {
            var probe = await ProbeAudioAsync(file, token);
            var sb = new StringBuilder();

            var srcLossless = src != null && IsLosslessSource(src);
            if (src == null) sb.Append("Источник: неизвестен");
            else
            {
                var codec = string.IsNullOrEmpty(src.Acodec) || src.Acodec == "none" ? src.Ext : src.Acodec;
                sb.Append($"Источник: {codec.ToUpperInvariant()}");
                if (src.Abr is > 0) sb.Append($" ~{src.Abr:0} kbps");
                sb.Append(srcLossless ? " (без потерь)" : " (со сжатием)");
            }
            if (probe.Length > 0) sb.Append($" → файл: {probe}");

            var targetLossless = requestedFormat.ToLowerInvariant() is "flac" or "wav" or "alac" or "aiff";
            if (targetLossless && src != null && !srcLossless)
                sb.Append(" ⚠️ Сайт отдаёт звук со сжатием, поэтому FLAC/WAV здесь «ненастоящий»: качество выше не станет, файл просто больше. " +
                          "Выберите «Оригинал» — получите тот же звук без лишней перекодировки.");
            else if (!targetLossless && requestedFormat != "best" && src is { Abr: > 0 } && !srcLossless && requestedBitrate > src.Abr * 1.25)
                sb.Append($" ℹ️ Исходник ~{src.Abr:0} kbps — выбранные {requestedBitrate} kbps качество не улучшат.");

            return sb.ToString();
        }

        /// <summary>Читает фактические параметры готового файла: кодек, частота, каналы, битрейт.</summary>
        private async Task<string> ProbeAudioAsync(string file, CancellationToken token)
        {
            try
            {
                var ffmpeg = ResolveFfmpeg();
                if (ffmpeg == null) return "";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                };
                foreach (var a in new[] { "-hide_banner", "-i", file }) psi.ArgumentList.Add(a);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                var (_, _, err) = await RunCaptureAsync(psi, cts.Token);

                var m = Regex.Match(err, @"Audio:\s*([^,\r\n]+),\s*(\d+)\s*Hz,\s*([^,\r\n]+)");
                if (!m.Success) return "";
                var br = Regex.Match(err, @"bitrate:\s*(\d+)\s*kb/s");
                var codec = m.Groups[1].Value.Trim().Split(' ')[0].ToUpperInvariant();
                var text = $"{codec}, {m.Groups[2].Value} Hz, {m.Groups[3].Value.Trim()}";
                if (br.Success) text += $", ~{br.Groups[1].Value} kbps";
                return text;
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }

        private static void ApplyFileInfo(DownloadItem item, string path)
        {
            var fileInfo = new FileInfo(path);
            item.FilePath = path;
            item.FileSize = fileInfo.Length;
            item.Format = Path.GetExtension(path).TrimStart('.').ToUpper();
        }

        // ───────────────────────── альбомы / плейлисты ─────────────────────────

        private sealed record PlaylistInfo(string? Title, string? Uploader, int Count, bool IsPlaylist);

        private static readonly Regex AlreadyRegex = new(@"\[download\]\s+(.+?)\s+has already been downloaded", RegexOptions.Compiled);
        private static readonly Regex FragmentRegex = new(@"\.f[\w-]+\.[A-Za-z0-9]{2,5}$", RegexOptions.Compiled);

        /// <summary>Быстрый запрос состава плейлиста (--flat-playlist): название, автор, количество треков.</summary>
        private async Task<PlaylistInfo?> GetPlaylistInfoAsync(string url, CancellationToken token, LogBuffer? log)
        {
            try
            {
                var args = new List<string> { "-J", "--flat-playlist", "--yes-playlist", "--no-warnings" };
                args.AddRange(CommonArgs());
                args.AddRange(JsRuntimeArgs());
                args.Add("--");
                args.Add(url);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(90));

                var (exit, json, err) = await RunCaptureAsync(CreatePsi(args), cts.Token);
                if (log != null)
                {
                    foreach (var l in err.Split('\n'))
                    {
                        var t = l.Trim();
                        if (t.Length > 0) log.Add(StderrPrefix(t) + t);
                    }
                }
                if (exit != 0 || string.IsNullOrWhiteSpace(json)) return null;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var type = JsonString(root, "_type");
                var uploader = JsonString(root, "uploader");
                if (string.IsNullOrEmpty(uploader)) uploader = JsonString(root, "channel");
                if (string.IsNullOrEmpty(uploader)) uploader = JsonString(root, "uploader_id");

                var count = 0;
                if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    count = entries.GetArrayLength();
                else if (JsonLong(root, "playlist_count") is long pc)
                    count = (int)pc;

                return new PlaylistInfo(JsonString(root, "title"), uploader, count, type == "playlist");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }   // таймаут запроса
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>«Исполнитель - Альбом»: понятное имя подпапки (раньше yt-dlp сам подставлял playlist_title и при ошибке получалась папка «NA»).</summary>
        private string BuildAlbumFolderName(PlaylistInfo? info, string url)
        {
            var title = info?.Title?.Trim();
            var uploader = info?.Uploader?.Trim();

            string name;
            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(uploader) &&
                !title.Contains(uploader, StringComparison.OrdinalIgnoreCase))
                name = $"{uploader} - {title}";
            else if (!string.IsNullOrEmpty(title)) name = title;
            else if (!string.IsNullOrEmpty(uploader)) name = uploader;
            else
            {
                var host = "Альбом";
                try { host = new Uri(url).Host.Replace("www.", ""); } catch { }
                name = $"{host} {DateTime.Now:yyyy-MM-dd HH-mm}";
            }

            name = SanitizeFileName(name);
            if (name.Length > 80) name = name[..80].TrimEnd();
            return string.IsNullOrWhiteSpace(name) ? "Альбом" : name;
        }

        private static string TrackTitle(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            name = Regex.Replace(name, @"\.f[\w-]+$", "");        // хвост временного потока .f137
            name = Regex.Replace(name, @"^\d+\s-\s", "");          // номер в начале
            return name;
        }

        private static string? FindTrackFile(string albumDir, int index)
        {
            try
            {
                return Directory.GetFiles(albumDir, $"{index:D3} - *")
                    .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                                && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                                && !f.EndsWith(".temp", StringComparison.OrdinalIgnoreCase)
                                && !FragmentRegex.IsMatch(f))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        public async Task<List<DownloadItem>> DownloadPlaylistAsync(
            string playlistUrl, string? formatId = null, bool isAudioOnly = false,
            int? startIndex = null, int? endIndex = null, CancellationToken token = default,
            string audioFormat = "mp3", int audioBitrate = 320, string videoContainer = "mkv",
            IProgress<string>? progress = null,
            DownloadItem? liveItem = null)
        {
            var items = new List<DownloadItem>();

            var playlistItem = liveItem ?? new DownloadItem();
            playlistItem.Url = playlistUrl;
            if (string.IsNullOrEmpty(playlistItem.Title))
                playlistItem.Title = "📚 Скачивание плейлиста...";
            playlistItem.Status = "Получение состава плейлиста...";
            playlistItem.Progress = 0;
            playlistItem.ConsoleLog = "";

            AddToHistory(playlistItem);
            SaveDownloadHistory();

            var log = new LogBuffer(playlistItem);
            Process? process = null;

            // общее состояние парсера (stdout и stderr приходят из разных потоков — всё под lock)
            var stateLock = new object();
            DownloadItem? currentTrack = null;
            var currentIndex = 0;
            var totalItems = 0;
            var currentFailed = false;
            var albumDir = "";
            var lastErrors = new Queue<string>();

            void FinalizeCurrentTrack(string? forcedStatus = null)
            {
                var t = currentTrack;
                if (t == null) return;

                if (forcedStatus != null) t.Status = forcedStatus;
                else if (currentFailed) t.Status = "❌ Ошибка";
                else
                {
                    if (string.IsNullOrEmpty(t.FilePath) || !File.Exists(t.FilePath))
                    {
                        var found = FindTrackFile(albumDir, currentIndex);
                        if (found != null) { t.FilePath = found; t.Title = TrackTitle(found); }
                    }
                    t.Progress = 100;
                    t.Status = "✅ Завершено";
                    try
                    {
                        if (!string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath))
                        {
                            t.FileSize = new FileInfo(t.FilePath).Length;
                            t.Format = Path.GetExtension(t.FilePath).TrimStart('.').ToUpper();
                        }
                    }
                    catch { }
                }

                currentTrack = null;
                currentFailed = false;
                SaveDownloadHistory();
            }

            // БАГ: раньше новая запись создавалась на КАЖДУЮ строку "[download] Destination:" — а для видео их две
            // (видеопоток и аудиопоток), поэтому в истории появлялись «треки»-обломки .f137/.f140.
            // Теперь трек создаётся по строке "Downloading item N of M".
            void StartTrack(int index)
            {
                FinalizeCurrentTrack();
                currentIndex = index;
                currentTrack = new DownloadItem
                {
                    Url = playlistUrl,
                    Title = $"Трек {index}",
                    Status = "⏳ Скачивание...",
                    Progress = 1
                };
                AddToHistory(currentTrack);
                items.Add(currentTrack);
                SaveDownloadHistory();
            }

            try
            {
                await PrepareForUrlAsync(playlistUrl);
                progress?.Report("Получаю состав плейлиста...");

                var info = await GetPlaylistInfoAsync(playlistUrl, token, log);

                // «Альбом» отмечен, а ссылка ведёт на один файл — качаем как обычный файл (раньше получалась папка «NA»)
                if (info != null && !info.IsPlaylist)
                {
                    progress?.Report("Ссылка ведёт на один файл — скачиваю как обычный.");
                    var single = await DownloadAsync(playlistUrl, formatId, isAudioOnly, null, progress, token,
                        audioFormat, audioBitrate, videoContainer, false, playlistItem);
                    items.Add(single);
                    return items;
                }

                if (info != null && info.Count == 0)
                {
                    playlistItem.Status = "❌ Плейлист пуст или недоступен";
                    log.Flush();
                    SaveDownloadHistory();
                    return items;
                }

                // подпапка альбома внутри папки загрузок
                var albumName = BuildAlbumFolderName(info, playlistUrl);
                albumDir = Path.Combine(_downloadPath, albumName);
                Directory.CreateDirectory(albumDir);

                totalItems = info?.Count ?? 0;
                playlistItem.Title = $"📚 {albumName}";
                playlistItem.Status = totalItems > 0 ? $"Альбом «{albumName}»: {totalItems} треков" : $"Альбом «{albumName}»";
                log.Add($"[ALBUM] Папка: {albumDir}");
                SaveDownloadHistory();

                // номер трека и название — в имени файла (исполнитель и альбом — в имени папки и в тегах)
                var outputPath = Path.Combine(albumDir, "%(playlist_index)03d - %(title).100s.%(ext)s");

                var args = BuildArguments(playlistUrl, outputPath, formatId, isAudioOnly, true, audioFormat, audioBitrate, videoContainer);

                var extra = new List<string>();
                if (startIndex.HasValue) extra.AddRange(new[] { "--playlist-start", startIndex.Value.ToString() });
                if (endIndex.HasValue) extra.AddRange(new[] { "--playlist-end", endIndex.Value.ToString() });
                args.InsertRange(0, extra);

                process = new Process { StartInfo = CreatePsi(args, workDir: albumDir), EnableRaisingEvents = true };

                process.OutputDataReceived += (s, e) =>
                {
                    var line = e.Data;
                    if (string.IsNullOrEmpty(line)) return;

                    log.Add(line);
                    progress?.Report(line);

                    lock (stateLock)
                    {
                        var hdr = ItemHeaderRegex.Match(line);
                        if (hdr.Success)
                        {
                            totalItems = int.Parse(hdr.Groups[2].Value);
                            StartTrack(int.Parse(hdr.Groups[1].Value));
                            playlistItem.Status = $"Трек {currentIndex} из {totalItems}...";
                            SaveDownloadHistory();
                            return;
                        }

                        var dest = DestinationRegex.Match(line);
                        if (dest.Success)
                        {
                            if (currentTrack == null) StartTrack(currentIndex + 1);
                            var path = dest.Groups[1].Value.Trim().Trim('"');
                            if (!FragmentRegex.IsMatch(path)) currentTrack!.FilePath = path;
                            currentTrack!.Title = TrackTitle(path);
                            return;
                        }

                        var already = AlreadyRegex.Match(line);
                        if (already.Success)
                        {
                            if (currentTrack == null) StartTrack(currentIndex + 1);
                            var path = already.Groups[1].Value.Trim().Trim('"');
                            currentTrack!.FilePath = path;
                            currentTrack.Title = TrackTitle(path);
                            currentTrack.Progress = 100;
                            return;
                        }

                        var finalPath = ExtractFinalPath(line);
                        if (finalPath != null && currentTrack != null)
                        {
                            currentTrack.FilePath = finalPath;
                            currentTrack.Title = TrackTitle(finalPath);
                            return;
                        }

                        // БАГ: double.TryParse без InvariantCulture — при русской локали "12.5" не парсилось
                        var pct = ProgressRegex.Match(line);
                        if (pct.Success && currentTrack != null &&
                            double.TryParse(pct.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                        {
                            currentTrack.Progress = (int)p;
                            currentTrack.Status = $"⏳ Скачивание: {p:F0}%";

                            if (totalItems > 0)
                            {
                                var overall = (int)(((currentIndex - 1) + p / 100.0) / totalItems * 100);
                                playlistItem.Progress = Math.Max(1, Math.Min(99, overall));
                                playlistItem.Status = $"Трек {currentIndex} из {totalItems}: {p:F0}%";
                            }
                        }
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    var line = e.Data;
                    if (string.IsNullOrEmpty(line)) return;

                    log.Add(StderrPrefix(line) + line);
                    progress?.Report(line);

                    lock (stateLock)
                    {
                        if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (currentTrack != null) currentFailed = true;
                            lastErrors.Enqueue(line);
                            if (lastErrors.Count > 3) lastErrors.Dequeue();
                        }
                    }
                };

                process.Start();
                lock (_processLock) _currentProcess = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var reg = token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                });

                await process.WaitForExitAsync(token);
                log.Flush();

                lock (stateLock) FinalizeCurrentTrack();

                var ok = items.Count(i => i.Status.StartsWith("✅"));
                var failed = items.Count(i => i.Status.StartsWith("❌"));
                var total = totalItems > 0 ? totalItems : items.Count;

                if (process.ExitCode == 0)
                {
                    playlistItem.Status = $"✅ Альбом скачан: {ok} треков → {albumDir}";
                    playlistItem.Progress = 100;
                }
                else if (ok > 0)
                {
                    // раньше любой ненулевой код (даже из-за одного недоступного видео) показывался как «ошибка плейлиста»
                    playlistItem.Status = $"⚠️ Скачано {ok} из {total}, ошибок: {failed} → {albumDir}";
                    playlistItem.Progress = 100;
                }
                else
                {
                    string errors;
                    lock (stateLock) errors = string.Join("\n", lastErrors);
                    playlistItem.Status = $"❌ Ошибка плейлиста (код {process.ExitCode})" + (errors.Length > 0 ? $": {errors}" : "");
                }

                SaveDownloadHistory();
            }
            catch (OperationCanceledException)
            {
                log.Flush();
                lock (stateLock) FinalizeCurrentTrack("⏹ Отменено");
                playlistItem.Status = "⏹ Отменено";
                SaveDownloadHistory();
            }
            catch (Exception ex)
            {
                log.Flush();
                lock (stateLock) FinalizeCurrentTrack("❌ Ошибка");
                playlistItem.Status = $"❌ Ошибка: {ex.Message}";
                playlistItem.ConsoleLog += "\n[EXCEPTION] " + ex;
                SaveDownloadHistory();
            }
            finally
            {
                lock (_processLock) { if (ReferenceEquals(_currentProcess, process)) _currentProcess = null; }
                process?.Dispose();
            }

            return items;
        }

        private static readonly Regex[] FinalPathRegexes =
        {
            new(@"\[ExtractAudio\]\s+Destination:\s+(.+)$", RegexOptions.Compiled),
            new(@"\[Merger\]\s+Merging formats into\s+""(.+)""", RegexOptions.Compiled),
            new(@"\[ffmpeg\]\s+Destination:\s+(.+)$", RegexOptions.Compiled)
        };

        // финальный путь после конвертации/слияния
        private string? ExtractFinalPath(string line)
        {
            foreach (var rx in FinalPathRegexes)
            {
                var m = rx.Match(line);
                if (!m.Success) continue;
                var path = m.Groups[1].Value.Trim().Trim('"');
                if (path.Length > 3 && (path.Contains(":\\") || path.StartsWith("/")))
                    return path;
            }
            return null;
        }

        private string GetUniqueOutputPath(string baseDir, string title, string extension)
        {
            extension = extension.TrimStart('.').ToLowerInvariant();
            var safeTitle = SanitizeFileName(title);
            if (string.IsNullOrWhiteSpace(safeTitle))
                safeTitle = $"download_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";

            if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);

            var candidate = Path.Combine(baseDir, $"{safeTitle}.{extension}");
            if (!File.Exists(candidate)) return candidate;

            var stamp = DateTime.Now.ToString("HH_mm_ss_dd_MM_yy");
            candidate = Path.Combine(baseDir, $"{safeTitle}_{stamp}.{extension}");

            int counter = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(baseDir, $"{safeTitle}_{stamp}_{counter}.{extension}");
                counter++;
            }
            return candidate;
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "download";
            foreach (var c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');
            if (fileName.Length > 150) fileName = fileName.Substring(0, 150);
            return fileName.Trim().TrimEnd('.');
        }

        private async Task WaitForFileReady(string filePath, CancellationToken token)
        {
            for (int i = 0; i < 10; i++)
            {
                try { using (File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) return; }
                catch (IOException) { await Task.Delay(200, token); }
            }
        }

        private string? TryFindFileByTitle(string title, string folder, bool isAudioOnly, string audioFormat)
        {
            if (string.IsNullOrEmpty(title) || !Directory.Exists(folder)) return null;
            try
            {
                var safeTitle = title;
                foreach (var c in Path.GetInvalidFileNameChars()) safeTitle = safeTitle.Replace(c, '_');

                string[] expectedExtensions = isAudioOnly
                    ? new[] { $".{audioFormat}", ".mp3", ".m4a", ".opus", ".ogg", ".flac", ".wav" }
                    : new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov" };

                foreach (var file in Directory.GetFiles(folder))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var ext = Path.GetExtension(file).ToLower();
                    if (!expectedExtensions.Contains(ext)) continue;

                    if (fileName.Contains(safeTitle, StringComparison.OrdinalIgnoreCase) ||
                        safeTitle.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
            catch { }
            return null;
        }

        public void CancelDownload()
        {
            lock (_processLock)
            {
                if (_currentProcess != null)
                {
                    try { if (!_currentProcess.HasExited) _currentProcess.Kill(entireProcessTree: true); } catch { }
                }
            }
        }

        private void ParseDownloadedFilePath(string line, List<string> paths)
        {
            if (string.IsNullOrEmpty(line)) return;

            foreach (var rx in PathRegexes)
            {
                var match = rx.Match(line);
                if (match.Success)
                {
                    var path = match.Groups[1].Value.Trim().Trim('"', '\'', ' ');
                    if (path.Length > 3 && (path.Contains(":\\") || path.StartsWith("/")))
                    {
                        if (!paths.Contains(path)) paths.Add(path);
                    }
                    break;
                }
            }
        }

        private List<string> JsRuntimeArgs()
        {
            // обновляемый deno (ставится из «Настроек») — самый надёжный вариант, он не зависит от установки Node.js
            if (File.Exists(Tools.ManagedDenoPath))
                return new List<string> { "--js-runtimes", $"deno:{Tools.ManagedDenoPath}" };

            // JS runtime для YouTube (node, не nodejs!)
            var nodePath = FindNodeJs();
            if (!string.IsNullOrEmpty(nodePath))
                return new List<string> { "--js-runtimes", $"node:{nodePath}" };

            var denoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Tools", "deno.exe");
            if (File.Exists(denoPath))
                return new List<string> { "--js-runtimes", $"deno:{denoPath}" };

            return new List<string> { "--js-runtimes", "node,deno,bun,quickjs" };
        }

        private List<string> BuildArguments(
            string url, string outputPath, string? formatId, bool isAudioOnly,
            bool isPlaylist, string audioFormat, int audioBitrate, string videoContainer)
        {
            var a = new List<string>
            {
                "--newline", "--no-mtime", "--windows-filenames",
                "--socket-timeout", "30", "--retries", "10", "--fragment-retries", "10", "--retry-sleep", "5",
                // параллельная загрузка DASH-фрагментов — заметно быстрее на YouTube
                "--concurrent-fragments", "4"
            };

            // TikTok: обход защиты через mobile API
            if (url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
            {
                a.AddRange(new[]
                {
                    "--extractor-args", "tiktok:api_hostname=api22-normal-c-useast2a.tiktokv.com",
                    "--extractor-args", "tiktok:app_info=2024",
                    "--user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    "--add-header", "Referer:https://www.tiktok.com/",
                    "--add-header", "Accept-Language:en-US,en;q=0.9",
                    "--add-header", "Sec-Ch-Ua:\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\""
                });
            }

            // YouTube: раньше принудительно ставился player_client=mweb + подделанный UA/Referer.
            // mweb требует GVS PO Token — без него yt-dlp пропускал все HTTPS-форматы и качал только 360p (формат 18).
            // Теперь клиентов выбирает сам yt-dlp (а токен подставляет плагин, если он установлен).

            // SoundCloud: раньше принудительно ставился "--downloader m3u8:ffmpeg". Через ffmpeg yt-dlp не печатает
            // проценты ([download] N%), поэтому полоса прогресса стояла на месте, а фрагменты не качались параллельно.
            // Встроенный HLS-загрузчик yt-dlp с SoundCloud работает штатно.

            a.AddRange(JsRuntimeArgs());
            a.AddRange(CommonArgs());                       // плагин PO-токенов + прокси
            if (isPlaylist) a.AddRange(new[] { "--yes-playlist", "--no-abort-on-error" });
            else a.Add("--no-playlist");

            var ffmpegExe = ResolveFfmpeg();
            if (ffmpegExe != null)
            {
                var ffmpegDir = Path.GetDirectoryName(ffmpegExe);
                if (!string.IsNullOrWhiteSpace(ffmpegDir))
                    a.AddRange(new[] { "--ffmpeg-location", ffmpegDir });
            }

            if (isAudioOnly)
            {
                a.AddRange(new[] { "-f", "bestaudio/best", "-x", "--audio-format", audioFormat.ToLowerInvariant(), "--embed-metadata" });

                // lossless-форматы и «best» (оригинал без перекодирования): битрейт не задаётся
                if (audioFormat.Equals("best", StringComparison.OrdinalIgnoreCase) ||
                    audioFormat.Equals("flac", StringComparison.OrdinalIgnoreCase) ||
                    audioFormat.Equals("wav", StringComparison.OrdinalIgnoreCase) ||
                    audioFormat.Equals("alac", StringComparison.OrdinalIgnoreCase) ||
                    audioFormat.Equals("aiff", StringComparison.OrdinalIgnoreCase)) { }
                else if (audioFormat.Equals("opus", StringComparison.OrdinalIgnoreCase)) a.AddRange(new[] { "--audio-quality", "0" });
                else if (audioBitrate > 0) a.AddRange(new[] { "--audio-quality", $"{audioBitrate}K" });
                else a.AddRange(new[] { "--audio-quality", "0" });
            }
            else
            {
                if (!(string.IsNullOrWhiteSpace(formatId) || formatId == "best"))
                    a.AddRange(new[] { "-f", formatId });

                bool needsMerge = string.IsNullOrWhiteSpace(formatId) ||
                                  formatId == "best" ||
                                  formatId.Contains('+') ||
                                  formatId.Contains("bestvideo", StringComparison.OrdinalIgnoreCase);

                if (needsMerge)
                    a.AddRange(new[] { "--merge-output-format", videoContainer.ToLowerInvariant() });
            }

            a.AddRange(new[] { "-o", outputPath });

            // "--" — всё дальше считается ссылкой, а не опцией (ссылка вида "-o ..." больше не опасна)
            a.Add("--");
            a.Add(url);
            return a;
        }

        private string? FindNodeJs()
        {
            var possiblePaths = new[]
            {
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files (x86)\nodejs\node.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "node", "node.exe"),
            };

            foreach (var path in possiblePaths)
                if (File.Exists(path)) return path;

            // node может стоять не в стандартной папке (nvm, scoop, portable) — ищем в PATH
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var p = Path.Combine(dir.Trim('"'), "node.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Сохраняет историю на диск (страница вызывает после переименования файла).</summary>
        public void SaveHistory() => SaveDownloadHistory();

        public void ClearHistory()
        {
            lock (_historyLock) _downloadHistory.Clear();
            SaveDownloadHistory();
        }

        public void RemoveFromHistory(DownloadItem item)
        {
            lock (_historyLock) _downloadHistory.Remove(item);
            SaveDownloadHistory();
        }

        // Парсинг прогресса: БАГ — double.TryParse без InvariantCulture: при русской локали "3.2" не разбиралось,
        // и полоса прогресса двигалась только при целых значениях (в итоге — рывком на 100%).
        private int _lastSavedProgress = -1;
        private void ParseProgress(string line, DownloadItem item)
        {
            var m = ProgressRegex.Match(line);
            if (!m.Success ||
                !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                return;

            item.Progress = (int)percent;
            item.Status = $"Загрузка: {percent.ToString("F1", CultureInfo.InvariantCulture)}%";

            if (Math.Abs(item.Progress - _lastSavedProgress) >= 5 || item.Progress >= 100)
            {
                SaveDownloadHistory();
                _lastSavedProgress = item.Progress;
            }
        }

        // ───────── история: запись потокобезопасная и атомарная ─────────
        private readonly object _saveLock = new();
        private const int MaxHistory = 300;
        private const int MaxSavedLogChars = 20_000;

        private void AddToHistory(DownloadItem item)
        {
            lock (_historyLock)
            {
                if (_downloadHistory.Contains(item)) return;
                _downloadHistory.Add(item);
                while (_downloadHistory.Count > MaxHistory)
                    _downloadHistory.Remove(_downloadHistory.OrderBy(x => x.AddedTime).First());
            }
        }

        // На диск уходит копия с обрезанным логом: раньше в JSON писались ВСЕ логи целиком при каждом сохранении
        private static DownloadItem CloneForSave(DownloadItem i)
        {
            var log = i.ConsoleLog ?? "";
            if (log.Length > MaxSavedLogChars) log = log[^MaxSavedLogChars..];
            return new DownloadItem
            {
                Title = i.Title, Url = i.Url, FilePath = i.FilePath, Status = i.Status, Progress = i.Progress,
                AddedTime = i.AddedTime, FileSize = i.FileSize, Format = i.Format, Resolution = i.Resolution,
                ConsoleLog = log, SourceInfo = i.SourceInfo
            };
        }

        private void SaveDownloadHistory()
        {
            try
            {
                string json;
                lock (_historyLock)
                    json = JsonSerializer.Serialize(_downloadHistory.Select(CloneForSave).ToList(), new JsonSerializerOptions { WriteIndented = true });

                var historyPath = GetHistoryFilePath();
                lock (_saveLock)
                {
                    // сначала во временный файл, потом подмена: обрыв записи не оставляет битый JSON
                    var tmp = historyPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, historyPath, overwrite: true);
                }
            }
            catch { }
        }

        private void LoadDownloadHistory()
        {
            try
            {
                var historyPath = GetHistoryFilePath();
                if (File.Exists(historyPath))
                {
                    var json = File.ReadAllText(historyPath);
                    var loaded = JsonSerializer.Deserialize<List<DownloadItem>>(json);
                    if (loaded != null) { _downloadHistory.Clear(); _downloadHistory.AddRange(loaded); }
                }
            }
            catch { }
        }

        private string GetHistoryFilePath()
        {
            var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DazzConv");
            if (!Directory.Exists(appDataPath)) Directory.CreateDirectory(appDataPath);
            return Path.Combine(appDataPath, "download_history.json");
        }
    }
}
