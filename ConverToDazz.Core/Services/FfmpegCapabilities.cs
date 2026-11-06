using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ConverToDazz.Core.Services
{
    /// <summary>Результат пробной конвертации одной пары «формат + кодеки».</summary>
    public record FormatTestResult(string Kind, string Ext, string VideoCodec, string AudioCodec, bool Ok, string Message);

    /// <summary>
    /// Что умеет именно ЭТА сборка ffmpeg: какие кодировщики и контейнеры в ней есть.
    /// Формы показывают только то, что точно сработает, а «Проверить форматы» в настройках делает реальную пробную конвертацию.
    /// </summary>
    public sealed class FfmpegCapabilities
    {
        private static readonly object Gate = new();
        private static FfmpegCapabilities? _cached;
        private static string _cachedKey = "";

        public HashSet<string> Encoders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Muxers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Loaded { get; private set; }

        /// <summary>Сопоставление «расширение файла → имя muxer в ffmpeg» для проверки поддержки контейнера.</summary>
        private static readonly Dictionary<string, string> MuxerByExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mp4"] = "mp4", ["mkv"] = "matroska", ["webm"] = "webm", ["mov"] = "mov", ["avi"] = "avi",
            ["m4v"] = "ipod", ["3gp"] = "3gp", ["3g2"] = "3g2", ["flv"] = "flv", ["ts"] = "mpegts", ["m2ts"] = "mpegts",
            ["ogv"] = "ogg", ["wmv"] = "asf", ["mpg"] = "mpeg", ["mpeg"] = "mpeg", ["vob"] = "vob", ["gif"] = "gif",
            // аудио
            ["mp3"] = "mp3", ["m4a"] = "ipod", ["aac"] = "adts", ["ogg"] = "ogg", ["opus"] = "opus", ["flac"] = "flac",
            ["wav"] = "wav", ["aiff"] = "aiff", ["alac"] = "ipod", ["wv"] = "wv", ["tta"] = "tta", ["caf"] = "caf",
            ["wma"] = "asf", ["ac3"] = "ac3", ["mp2"] = "mp2", ["amr"] = "amr"
        };

        public bool HasEncoder(string name) => !Loaded || name == "copy" || name == "gif" || Encoders.Contains(name);
        public bool CanMux(string ext) => !Loaded || !MuxerByExt.TryGetValue(ext.TrimStart('.'), out var m) || Muxers.Contains(m);

        public bool IsVideoContainerUsable(string ext)
        {
            var c = FormatCatalog.GetVideoContainer(ext);
            return c != null && CanMux(ext) && c.VideoCodecs.Any(HasEncoder);
        }

        public bool IsAudioFormatUsable(string ext)
        {
            var f = FormatCatalog.GetAudioFormat(ext);
            return f != null && CanMux(ext) && HasEncoder(f.Codec);
        }

        // ───────────────────────── загрузка возможностей ─────────────────────────

        public static async Task<FfmpegCapabilities> GetAsync(string ffmpegPath, CancellationToken ct = default)
        {
            string key;
            try { key = ffmpegPath + "|" + File.GetLastWriteTimeUtc(ffmpegPath).Ticks; } catch { key = ffmpegPath; }

            lock (Gate) { if (_cached != null && _cachedKey == key) return _cached; }

            var caps = new FfmpegCapabilities();
            try
            {
                if (File.Exists(ffmpegPath))
                {
                    var enc = await RunAsync(ffmpegPath, new[] { "-hide_banner", "-encoders" }, ct);
                    foreach (Match m in Regex.Matches(enc, @"^\s*[VAS][A-Z.]{5}\s+(\S+)", RegexOptions.Multiline))
                        caps.Encoders.Add(m.Groups[1].Value);

                    var mux = await RunAsync(ffmpegPath, new[] { "-hide_banner", "-muxers" }, ct);
                    foreach (Match m in Regex.Matches(mux, @"^\s*[D ]E\s+(\S+)", RegexOptions.Multiline))
                        caps.Muxers.Add(m.Groups[1].Value);

                    caps.Loaded = caps.Encoders.Count > 0 && caps.Muxers.Count > 0;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* не удалось прочитать — считаем, что ограничений нет */ }

            lock (Gate) { _cached = caps; _cachedKey = key; }
            return caps;
        }

        private static async Task<string> RunAsync(string exe, string[] args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg не запустился");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var errTask = p.StandardError.ReadToEndAsync();
            var text = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            await errTask;
            return text;
        }

        // ───────────────────────── реальная проверка форматов ─────────────────────────

        private static string ResultsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DazzConv", "tools", "format_check.json");

        public static List<FormatTestResult>? LoadLastResults()
        {
            try { return File.Exists(ResultsPath) ? JsonSerializer.Deserialize<List<FormatTestResult>>(File.ReadAllText(ResultsPath)) : null; }
            catch { return null; }
        }

        /// <summary>
        /// Для КАЖДОГО видеоконтейнера и аудиоформата кодирует крошечный тестовый файл с настройками по умолчанию.
        /// Результат сохраняется и используется формами: формат, который на этом компьютере не работает, будет помечен.
        /// </summary>
        public static async Task<List<FormatTestResult>> SelfTestAsync(string ffmpegPath, IProgress<string>? status = null, CancellationToken ct = default)
        {
            var results = new List<FormatTestResult>();
            var tmp = Path.Combine(Path.GetTempPath(), "DazzConv_FormatTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);

            try
            {
                var jobs = new List<(string Kind, string Ext, string V, string A, string[] Args, string OutExt)>();

                foreach (var c in FormatCatalog.VideoContainers)
                {
                    if (c.Ext == "gif") continue;   // GIF проверяется отдельно ниже
                    var args = new List<string>
                    {
                        "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=15:duration=0.5",
                        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=0.5",
                        "-c:v", c.DefaultVideo, "-pix_fmt", "yuv420p"
                    };
                    if (c.DefaultVideo == "prores_ks") { args.RemoveRange(args.Count - 2, 2); }
                    args.AddRange(new[] { "-c:a", c.DefaultAudio });
                    if (c.ExtraArgs != null) args.AddRange(c.ExtraArgs);
                    jobs.Add(("video", c.Ext, c.DefaultVideo, c.DefaultAudio, args.ToArray(), c.Ext));
                }

                foreach (var f in FormatCatalog.AudioFormats)
                {
                    var args = new List<string> { "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=0.5" };
                    if (f.Ext == "amr") args.AddRange(new[] { "-ac", "1", "-ar", "8000" });
                    args.AddRange(new[] { "-c:a", f.Codec });
                    if (f.HasBitrate) args.AddRange(new[] { "-b:a", $"{f.DefaultBitrate}k" });
                    if (f.ExtraArgs != null) args.AddRange(f.ExtraArgs);
                    jobs.Add(("audio", f.Ext, "", f.Codec, args.ToArray(), f.OutputExt));
                }

                var i = 0;
                foreach (var job in jobs)
                {
                    ct.ThrowIfCancellationRequested();
                    i++;
                    status?.Report($"Проверка {i} из {jobs.Count}: {job.Kind} .{job.Ext}");

                    var outFile = Path.Combine(tmp, $"t{i}.{job.OutExt}");
                    var psi = new ProcessStartInfo { FileName = ffmpegPath, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    foreach (var a in new[] { "-hide_banner", "-y" }.Concat(job.Args).Append(outFile)) psi.ArgumentList.Add(a);

                    try
                    {
                        using var p = Process.Start(psi)!;
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(60));
                        var errTask = p.StandardError.ReadToEndAsync();
                        var outTask = p.StandardOutput.ReadToEndAsync();
                        try { await p.WaitForExitAsync(cts.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            try { p.Kill(true); } catch { }
                            results.Add(new FormatTestResult(job.Kind, job.Ext, job.V, job.A, false, "Таймаут проверки"));
                            continue;
                        }
                        var err = await errTask; await outTask;

                        var ok = p.ExitCode == 0 && File.Exists(outFile) && new FileInfo(outFile).Length > 0;
                        var msg = ok ? "Работает" : LastMeaningfulLine(err);
                        results.Add(new FormatTestResult(job.Kind, job.Ext, job.V, job.A, ok, msg));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { results.Add(new FormatTestResult(job.Kind, job.Ext, job.V, job.A, false, ex.Message)); }
                }

                // GIF (палитровый фильтр)
                try
                {
                    var gif = Path.Combine(tmp, "t_gif.gif");
                    var psi = new ProcessStartInfo { FileName = ffmpegPath, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    foreach (var a in new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=10:duration=0.5",
                        "-vf", "split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse", gif }) psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi)!;
                    var e = p.StandardError.ReadToEndAsync(); var o = p.StandardOutput.ReadToEndAsync();
                    await p.WaitForExitAsync(ct); await e; await o;
                    var ok = p.ExitCode == 0 && File.Exists(gif);
                    results.Add(new FormatTestResult("video", "gif", "gif", "", ok, ok ? "Работает" : "GIF не получился"));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { results.Add(new FormatTestResult("video", "gif", "gif", "", false, ex.Message)); }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);
                File.WriteAllText(ResultsPath, JsonSerializer.Serialize(results));
            }
            catch { }

            return results;
        }

        private static string LastMeaningfulLine(string err)
        {
            var line = err.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).LastOrDefault();
            return string.IsNullOrEmpty(line) ? "Ошибка ffmpeg" : (line.Length > 160 ? line[..160] : line);
        }

        /// <summary>Формат отмечен как нерабочий последней проверкой (если проверка ещё не запускалась — считаем рабочим).</summary>
        public static bool IsMarkedBroken(string kind, string ext)
        {
            var last = LoadLastResults();
            var r = last?.FirstOrDefault(x => x.Kind == kind && x.Ext.Equals(ext, StringComparison.OrdinalIgnoreCase));
            return r != null && !r.Ok;
        }
    }
}
