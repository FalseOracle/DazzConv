using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private readonly string _ytDlpPath;
        private string _downloadPath;
        private readonly List<DownloadItem> _downloadHistory;
        private string? _proxyUrl;

        public YtDlpService()
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            _ytDlpPath = Path.Combine(basePath, "Resources", "Tools", "yt-dlp.exe");

            if (!File.Exists(_ytDlpPath))
            {
                _ytDlpPath = Path.Combine(basePath, "yt-dlp.exe");
            }

            if (!File.Exists(_ytDlpPath))
            {
                _ytDlpPath = "yt-dlp";
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

        // Установка прокси (для VPN/TUN режима)
        public void SetProxy(string? proxyUrl)
        {
            _proxyUrl = proxyUrl;

        }

        // Тестирование подключения к YouTube через прокси
        public async Task<bool> TestConnectionAsync(string testUrl, CancellationToken token = default)
        {
            try
            {
                var args = $"--simulate --no-download --socket-timeout 10 \"{testUrl}\"";

                if (!string.IsNullOrEmpty(_proxyUrl))
                {
                    args += $" --proxy \"{_proxyUrl}\"";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                // Ждём максимум 15 секунд
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
                    await process.WaitForExitAsync(timeoutCts.Token);
                }

                // Если процесс завершился с кодом 0 или получил информацию о видео - всё ок
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public string DownloadPath
        {
            get => _downloadPath;
            set
            {
                _downloadPath = value;
                if (!Directory.Exists(_downloadPath))
                    Directory.CreateDirectory(_downloadPath);
            }
        }

        public void SetDownloadPath(string path)
        {
            DownloadPath = path;
        }

        public IReadOnlyList<DownloadItem> DownloadHistory => _downloadHistory.AsReadOnly();

        // Получение всех доступных форматов с подробной информацией
        public async Task<List<VideoFormat>> GetAvailableFormatsAsync(string url, CancellationToken token = default)
        {
            var formats = new List<VideoFormat>();

            var args = $"-j --no-playlist \"{url}\"";

            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!string.IsNullOrEmpty(_proxyUrl))
            {
                psi.Arguments += $" --proxy \"{_proxyUrl}\"";
            }

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();

                var jsonOutput = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(token);

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(jsonOutput))
                {
                    using var doc = JsonDocument.Parse(jsonOutput);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("formats", out var formatsArray))
                    {
                        foreach (var format in formatsArray.EnumerateArray())
                        {
                            var videoFormat = new VideoFormat
                            {
                                FormatId = format.GetProperty("format_id").GetString() ?? "",
                                Extension = format.GetProperty("ext").GetString() ?? "",
                                Resolution = format.GetProperty("resolution").GetString() ?? "unknown",
                                Vcodec = format.GetProperty("vcodec").GetString() ?? "none",
                                Acodec = format.GetProperty("acodec").GetString() ?? "none",
                                Filesize = format.TryGetProperty("filesize", out var size) ? size.GetInt64() : null,
                                FormatNote = format.TryGetProperty("format_note", out var note) ? note.GetString() ?? "" : "",
                                Fps = format.TryGetProperty("fps", out var fps) ? fps.GetDouble() : null,
                                Height = format.TryGetProperty("height", out var h) ? h.GetInt32() : null
                            };

                            formats.Add(videoFormat);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting formats: {ex.Message}");
            }

            // Сортируем по качеству (высшее primero)
            return formats.OrderByDescending(f => f.Height ?? 0).ThenByDescending(f => f.Filesize ?? 0).ToList();
        }

        // Проверка является ли URL плейлистом
        public async Task<bool> IsPlaylistAsync(string url, CancellationToken token = default)
        {
            var args = $"--playlist-items 0 --simulate \"{url}\"";

            if (!string.IsNullOrEmpty(_proxyUrl))
            {
                args += $" --proxy \"{_proxyUrl}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();
                var errorOutput = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(token);

                return errorOutput.Contains("playlist") || errorOutput.Contains("Downloading playlist");
            }
            catch
            {
                return false;
            }
        }

        // Получение информации о плейлисте
        public async Task<(int count, string title)> GetPlaylistInfoAsync(string url, CancellationToken token = default)
        {
            var args = $"--flat-playlist --print \"%(playlist)s\\t%(playlist_count)s\" \"{url}\"";

            if (!string.IsNullOrEmpty(_proxyUrl))
            {
                args += $" --proxy \"{_proxyUrl}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = args,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(token);

                if (!string.IsNullOrEmpty(output))
                {
                    var parts = output.Trim().Split('\t');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var count))
                    {
                        return (count, parts[0]);
                    }
                }
            }
            catch { }

            return (0, "");
        }

        public async Task<DownloadItem> DownloadAsync(
    string url,
    string? formatId = null,
    bool isAudioOnly = false,
    string? customOutputPath = null,
    IProgress<string>? progress = null,
    CancellationToken token = default,
    string audioFormat = "mp3",
    int audioBitrate = 320)
        {
            var item = new DownloadItem
            {
                Url = url,
                Status = "Получение информации...",
                Progress = 0
            };

            _downloadHistory.Add(item);
            SaveDownloadHistory();

            try
            {
                // Получаем информацию о видео
                var infoArgs = $"-j --no-playlist \"{url}\"";
                if (!string.IsNullOrEmpty(_proxyUrl))
                    infoArgs += $" --proxy \"{_proxyUrl}\"";

                var psiInfo = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = infoArgs,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var infoProcess = new Process { StartInfo = psiInfo })
                {
                    infoProcess.Start();
                    var jsonOutput = await infoProcess.StandardOutput.ReadToEndAsync();
                    await infoProcess.WaitForExitAsync();

                    if (infoProcess.ExitCode == 0 && !string.IsNullOrEmpty(jsonOutput))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(jsonOutput);
                            var root = doc.RootElement;

                            if (root.TryGetProperty("title", out var titleProp))
                                item.Title = titleProp.GetString() ?? "Без названия";
                            else if (root.TryGetProperty("fulltitle", out var fullTitleProp))
                                item.Title = fullTitleProp.GetString() ?? "Без названия";
                            else
                                item.Title = Path.GetFileName(url);
                        }
                        catch
                        {
                            item.Title = Path.GetFileName(url);
                        }
                    }
                }

                if (string.IsNullOrEmpty(item.Title))
                    item.Title = Path.GetFileName(url);

                var outputPath = customOutputPath ?? Path.Combine(_downloadPath, "%(title)s.%(ext)s");

                // НОВОЕ: Запоминаем файлы в папке ДО скачивания (для fallback)
                var filesBeforeDownload = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(_downloadPath))
                {
                    foreach (var f in Directory.GetFiles(_downloadPath))
                    {
                        try { filesBeforeDownload[f] = File.GetLastWriteTimeUtc(f); } catch { }
                    }
                }

                var args = BuildArguments(url, outputPath, formatId, isAudioOnly, audioFormat, audioBitrate);

                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _downloadPath
                };

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                var outputLines = new List<string>();
                var downloadedFilePaths = new List<string>();

                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputLines.Add(e.Data);
                        progress?.Report(e.Data);
                        ParseProgress(e.Data, item);
                        ParseDownloadedFilePath(e.Data, downloadedFilePaths);
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputLines.Add(e.Data);
                        progress?.Report(e.Data);
                        ParseDownloadedFilePath(e.Data, downloadedFilePaths);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                item.Status = "Загрузка...";
                SaveDownloadHistory();

                await process.WaitForExitAsync(token);

                // ===== ОПРЕДЕЛЕНИЕ СКАЧАННОГО ФАЙЛА =====
                if (process.ExitCode == 0)
                {
                    item.Status = "✅ Завершено";
                    item.Progress = 100;

                    string? downloadedFile = null;

                    // Способ 1: Путь из вывода yt-dlp (самый надёжный)
                    if (downloadedFilePaths.Any())
                    {
                        downloadedFile = downloadedFilePaths.Last();
                        System.Diagnostics.Debug.WriteLine($"[YtDlp] Способ 1 (из вывода): {downloadedFile}");
                    }
                    // Способ 2: Ищем НОВЫЙ файл (которого не было до скачивания или он изменился)
                    else if (Directory.Exists(_downloadPath))
                    {
                        // Ждём 1 секунду чтобы файл точно записался на диск
                        await Task.Delay(1000, token);

                        var currentFiles = Directory.GetFiles(_downloadPath);
                        var newOrModifiedFiles = new List<(string path, DateTime time, long size)>();

                        foreach (var file in currentFiles)
                        {
                            try
                            {
                                var fi = new FileInfo(file);
                                var lastWrite = fi.LastWriteTimeUtc;

                                // Файл новый или изменился после начала скачивания
                                bool isNew = !filesBeforeDownload.ContainsKey(file);
                                bool isModified = filesBeforeDownload.TryGetValue(file, out var oldTime) && lastWrite > oldTime;

                                if ((isNew || isModified) && fi.Length > 0)
                                {
                                    newOrModifiedFiles.Add((file, lastWrite, fi.Length));
                                }
                            }
                            catch { }
                        }

                        if (newOrModifiedFiles.Any())
                        {
                            // Берём самый свежий и самый большой
                            downloadedFile = newOrModifiedFiles
                                .OrderByDescending(f => f.time)
                                .ThenByDescending(f => f.size)
                                .First().path;

                            System.Diagnostics.Debug.WriteLine($"[YtDlp] Способ 2 (новый/изменённый): {downloadedFile}");
                        }
                    }

                    // НОВОЕ: Финальная проверка и обновление размера
                    if (!string.IsNullOrEmpty(downloadedFile) && File.Exists(downloadedFile))
                    {
                        // Ждём пока файл полностью запишется (иногда Windows блокирует)
                        await WaitForFileReady(downloadedFile, token);

                        var fileInfo = new FileInfo(downloadedFile);
                        item.FilePath = downloadedFile;
                        item.FileSize = fileInfo.Length;
                        item.Format = Path.GetExtension(downloadedFile).TrimStart('.').ToUpper();

                        System.Diagnostics.Debug.WriteLine($"[YtDlp] ИТОГ: {item.FilePath} ({item.FileSize} байт)");
                    }
                    else
                    {
                        // НОВОЕ: Последняя попытка — ищем по имени в выводе yt-dlp
                        downloadedFile = TryFindFileByTitle(item.Title, _downloadPath, isAudioOnly, audioFormat);

                        if (!string.IsNullOrEmpty(downloadedFile) && File.Exists(downloadedFile))
                        {
                            var fileInfo = new FileInfo(downloadedFile);
                            item.FilePath = downloadedFile;
                            item.FileSize = fileInfo.Length;
                            item.Format = Path.GetExtension(downloadedFile).TrimStart('.').ToUpper();
                            System.Diagnostics.Debug.WriteLine($"[YtDlp] Способ 3 (по имени): {downloadedFile}");
                        }
                        else
                        {
                            item.Status = "⚠️ Завершено, но файл не найден. Проверьте папку загрузок.";
                            System.Diagnostics.Debug.WriteLine("[YtDlp] Файл не найден ни одним способом");
                        }
                    }
                }
                else
                {
                    var errorMsg = string.Join("\n", outputLines.TakeLast(3));
                    item.Status = $"❌ Ошибка (код {process.ExitCode})";
                    if (!string.IsNullOrEmpty(errorMsg))
                        item.Status += $": {errorMsg}";
                }

                SaveDownloadHistory();
                return item;
            }
            catch (Exception ex)
            {
                item.Status = $"❌ Ошибка: {ex.Message}";
                SaveDownloadHistory();
                throw;
            }
        }

        // НОВОЕ: Ожидание пока файл будет готов к чтению
        private async Task WaitForFileReady(string filePath, CancellationToken token)
        {
            var maxAttempts = 10;
            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    using (var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        return; // Файл готов
                    }
                }
                catch (IOException)
                {
                    // Файл ещё занят — ждём
                    await Task.Delay(200, token);
                }
            }
        }

        // НОВОЕ: Поиск файла по названию видео
        private string? TryFindFileByTitle(string title, string folder, bool isAudioOnly, string audioFormat)
        {
            if (string.IsNullOrEmpty(title) || !Directory.Exists(folder))
                return null;

            try
            {
                // Очищаем название от недопустимых символов
                var safeTitle = title;
                foreach (var c in Path.GetInvalidFileNameChars())
                    safeTitle = safeTitle.Replace(c, '_');

                // Определяем ожидаемое расширение
                string[] expectedExtensions;
                if (isAudioOnly)
                {
                    expectedExtensions = new[] { $".{audioFormat}", ".mp3", ".m4a", ".opus", ".ogg", ".flac", ".wav" };
                }
                else
                {
                    expectedExtensions = new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov" };
                }

                // Ищем файлы с похожим именем
                foreach (var file in Directory.GetFiles(folder))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var ext = Path.GetExtension(file).ToLower();

                    // Проверяем что расширение подходит
                    if (!expectedExtensions.Contains(ext))
                        continue;

                    // Проверяем что имя файла содержит название видео
                    if (fileName.Contains(safeTitle, StringComparison.OrdinalIgnoreCase) ||
                        safeTitle.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
            }
            catch { }

            return null;
        }

        public void CancelDownload()
        {
            lock (_processLock)
            {
                if (_currentProcess != null && !_currentProcess.HasExited)
                {
                    try
                    {
                        _currentProcess.Kill(entireProcessTree: true);
                        System.Diagnostics.Debug.WriteLine("[YtDlp] Процесс принудительно завершён");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[YtDlp] Ошибка завершения процесса: {ex.Message}");
                    }
                }
            }
        }


        // НОВОЕ: Парсинг пути к скачанному файлу из вывода yt-dlp
        private void ParseDownloadedFilePath(string line, List<string> paths)
        {
            if (string.IsNullOrEmpty(line)) return;

            // yt-dlp выводит пути в разных форматах:
            // [download] Destination: C:\path\to\file.mp4
            // [Merger] Merging formats into "C:\path\to\file.mp4"
            // [ExtractAudio] Destination: C:\path\to\file.mp3
            // [ffmpeg] Destination: C:\path\to\file.mp4

            var patterns = new[]
            {
        @"\[download\]\s+Destination:\s+(.+)$",
        @"\[Merger\]\s+Merging formats into\s+""(.+)""",
        @"\[Merger\]\s+Merging formats into\s+(.+)$",
        @"\[ExtractAudio\]\s+Destination:\s+(.+)$",
        @"\[ffmpeg\]\s+Destination:\s+(.+)$",
        @"\[ExtractAudio\]\s+""(.+)""",
        @"\[Merger\]\s+""(.+)"""
    };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(line, pattern);
                if (match.Success)
                {
                    var path = match.Groups[1].Value.Trim().Trim('"');

                    // Проверяем что путь выглядит как реальный путь к файлу
                    if (path.Length > 3 && (path.Contains(":\\") || path.StartsWith("/")))
                    {
                        // Убираем возможные кавычки в конце
                        path = path.Trim('"', '\'', ' ');

                        if (!paths.Contains(path))
                        {
                            paths.Add(path);
                            System.Diagnostics.Debug.WriteLine($"[YtDlp Parse] Найден путь: {path}");
                        }
                    }
                    break;
                }
            }
        }

        private string BuildArguments(string url, string outputPath, string? formatId, bool isAudioOnly,
            string audioFormat = "mp3", int audioBitrate = 320)
        {
            var args = "--no-mtime --no-playlist ";

            // Прокси
            if (!string.IsNullOrEmpty(_proxyUrl))
            {
                args += $"--proxy \"{_proxyUrl}\" ";
            }

            if (isAudioOnly)
            {
                args += $"-x --audio-format {audioFormat} --audio-quality {audioBitrate}k ";
            }
            else
            {
                // Скачиваем лучшее качество + лучший аудио, затем合并
                if (string.IsNullOrEmpty(formatId))
                {
                    // Лучшее видео + лучший аудио
                    args += "-f \"bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best\" ";
                    args += "--merge-output-format mp4 ";
                }
                else
                {
                    // Конкретный формат
                    args += $"-f {formatId} ";
                }
            }

            args += $"-o \"{outputPath}\" \"{url}\"";
            return args;
        }

        // Скачивание плейлиста
        public async Task<List<DownloadItem>> DownloadPlaylistAsync(
            string playlistUrl,
            string? formatId = null,
            bool isAudioOnly = false,
            int? startIndex = null,
            int? endIndex = null,
            CancellationToken token = default)
        {
            var items = new List<DownloadItem>();

            var args = "--flat-playlist --print \"%(id)s\\t%(title)s\\t%(url)s\\t%(resolution)s\" ";

            if (startIndex.HasValue)
            {
                args += $"--playlist-start {startIndex.Value} ";
            }
            if (endIndex.HasValue)
            {
                args += $"--playlist-end {endIndex.Value} ";
            }

            if (!string.IsNullOrEmpty(_proxyUrl))
            {
                args += $"--proxy \"{_proxyUrl}\" ";
            }

            args += $"\"{playlistUrl}\"";

            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = args,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _downloadPath
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var playlistItems = new List<(string id, string title, string url, string resolution)>();

            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 3)
                    {
                        playlistItems.Add((parts[0], parts[1], parts[2], parts.Length > 3 ? parts[3] : ""));
                    }
                }
            }

            await process.WaitForExitAsync(token);

            foreach (var (id, title, url, resolution) in playlistItems)
            {
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    var item = await DownloadAsync(url, formatId, isAudioOnly, token: token);
                    items.Add(item);
                }
                catch (Exception ex)
                {
                    items.Add(new DownloadItem
                    {
                        Url = url,
                        Title = title,
                        Status = $"Ошибка: {ex.Message}"
                    });
                }
            }

            return items;
        }

        public void ClearHistory()
        {
            _downloadHistory.Clear();
            SaveDownloadHistory();
        }

        public void RemoveFromHistory(DownloadItem item)
        {
            _downloadHistory.Remove(item);
            SaveDownloadHistory();
        }

        private int _lastSavedProgress = -1;

        private void ParseProgress(string line, DownloadItem item)
        {
            var match = Regex.Match(line, @"\[download\]\s+(\d+\.?\d*)%");
            if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
            {
                item.Progress = (int)percent;
                item.Status = $"Загрузка: {percent:F1}%";

                if (Math.Abs(item.Progress - _lastSavedProgress) >= 5 || item.Progress >= 100)
                {
                    SaveDownloadHistory();
                    _lastSavedProgress = item.Progress;
                }
            }

            var fileMatch = Regex.Match(line, @"(\d+\.?\d*)(MiB|KiB|GiB)");
            if (fileMatch.Success && double.TryParse(fileMatch.Groups[1].Value, out var size))
            {
                var unit = fileMatch.Groups[2].Value;
                item.FileSize = unit switch
                {
                    "KiB" => (long)(size * 1024),
                    "MiB" => (long)(size * 1024 * 1024),
                    "GiB" => (long)(size * 1024 * 1024 * 1024),
                    _ => item.FileSize
                };
            }
        }

        private string? FindDownloadedFile(string outputPathPattern)
        {
            var directory = Path.GetDirectoryName(outputPathPattern);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return null;

            var files = Directory.GetFiles(directory);
            return files.Length > 0 ? files[files.Length - 1] : null;
        }

        private void SaveDownloadHistory()
        {
            try
            {
                var historyPath = GetHistoryFilePath();
                var json = JsonSerializer.Serialize(_downloadHistory,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(historyPath, json);
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
                    if (loaded != null)
                    {
                        _downloadHistory.Clear();
                        _downloadHistory.AddRange(loaded);
                    }
                }
            }
            catch { }
        }

        private string GetHistoryFilePath()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DazzConv");

            if (!Directory.Exists(appDataPath))
                Directory.CreateDirectory(appDataPath);

            return Path.Combine(appDataPath, "download_history.json");
        }
    }
}