using ConverToDazz.Core.Models;
using ConverToDazz.Core.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DazzConv.Services
{
    /// <summary>
    /// Единственная реализация конвертации видео через FFmpeg.
    /// (старый VideoConverterService теперь — тонкая обёртка над ней)
    /// </summary>
    public class FfmpegVideoConverterService : IVideoConverterService
    {
        private static readonly Regex TimeRegex =
            new(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
        private static readonly Regex DurationRegex =
            new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

        private static readonly Dictionary<string, string> NvencPresets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ultrafast"] = "p1", ["superfast"] = "p2", ["veryfast"] = "p3",
            ["faster"] = "p4", ["fast"] = "p5", ["medium"] = "p6",
            ["slow"] = "p7", ["slower"] = "p7", ["veryslow"] = "p7"
        };

        private readonly FFmpegPathService _ffmpegService;

        public FfmpegVideoConverterService(FFmpegPathService ffmpegService)
        {
            _ffmpegService = ffmpegService;
        }

        public Task ConvertAsync(
            string inputFile,
            string outputFile,
            double durationSeconds,
            IProgress<int> progress,
            VideoConversionOptions options,
            CancellationToken token,
            VideoExtraOptions? extra = null)
            => ConvertAsync(_ffmpegService.GetFFmpegPath(), inputFile, outputFile,
                            durationSeconds, progress, options, token, extra);

        /// <summary>Общее ядро: используется и новым сервисом, и обёрткой VideoConverterService.</summary>
        public static async Task ConvertAsync(
            string ffmpegPath,
            string inputFile,
            string outputFile,
            double durationSeconds,
            IProgress<int>? progress,
            VideoConversionOptions options,
            CancellationToken token,
            VideoExtraOptions? extra = null)
        {
            if (!File.Exists(ffmpegPath))
                throw new FileNotFoundException($"FFmpeg не найден: {ffmpegPath}");
            if (!File.Exists(inputFile))
                throw new FileNotFoundException($"Файл не найден: {inputFile}");

            var outputDir = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir); // no-op, если папка уже есть

            bool isGif = string.Equals(options.VideoCodec, "gif", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetExtension(outputFile), ".gif", StringComparison.OrdinalIgnoreCase);

            var args = isGif
                ? BuildGifArguments(inputFile, outputFile, options)
                : BuildArguments(inputFile, outputFile, options, extra);

            await RunFfmpegAsync(ffmpegPath, args, outputFile, durationSeconds, progress, token);
        }

        // ───────────────────────── запуск процесса ─────────────────────────

        private static async Task RunFfmpegAsync(
            string ffmpegPath,
            List<string> args,
            string outputFile,
            double durationSeconds,
            IProgress<int>? progress,
            CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // ArgumentList сам экранирует кавычки/пробелы в путях
            foreach (var a in args) psi.ArgumentList.Add(a);

            Debug.WriteLine("ffmpeg " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

            using var process = new Process { StartInfo = psi };

            // Хранится только хвост лога (раньше строка росла бесконечно: O(n²) конкатенации)
            var tail = new Queue<string>();
            double totalSeconds = durationSeconds;
            int lastPercent = -1;

            process.ErrorDataReceived += (_, e) =>
            {
                var line = e.Data;
                if (string.IsNullOrWhiteSpace(line)) return;

                lock (tail)
                {
                    tail.Enqueue(line);
                    if (tail.Count > 30) tail.Dequeue();
                }

                if (totalSeconds <= 0)
                {
                    var dm = DurationRegex.Match(line);
                    if (dm.Success) totalSeconds = ToSeconds(dm);
                    return;
                }

                var tm = TimeRegex.Match(line);
                if (!tm.Success) return;

                var percent = (int)Math.Clamp(ToSeconds(tm) / totalSeconds * 100, 0, 100);
                if (percent == lastPercent) return;   // не засоряем UI одинаковыми значениями
                lastPercent = percent;
                progress?.Report(percent);
            };
            process.OutputDataReceived += (_, _) => { }; // дренируем stdout, чтобы процесс не завис

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            // Отмена реально убивает ffmpeg (раньше процесс продолжал работать в фоне)
            using var reg = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                TryDelete(outputFile);   // недописанный файл
                throw;
            }

            if (process.ExitCode != 0)
            {
                TryDelete(outputFile);
                string lastLines;
                lock (tail) lastLines = string.Join("\n", tail.TakeLast(3));
                // (раньше TakeLast(5).FirstOrDefault() возвращал 5-ю строку с конца, а не последнюю)
                throw new Exception($"FFmpeg ошибка (код {process.ExitCode}):\n{(lastLines.Length == 0 ? "Неизвестная ошибка" : lastLines)}");
            }

            if (!File.Exists(outputFile))
                throw new Exception($"Выходной файл не создан: {outputFile}");

            progress?.Report(100);
        }

        private static double ToSeconds(Match m) =>
            int.Parse(m.Groups[1].Value) * 3600
            + int.Parse(m.Groups[2].Value) * 60
            + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ───────────────────────── аргументы ─────────────────────────

        // Аргументы строятся по КАТАЛОГУ форматов (FormatCatalog): допустимые кодеки контейнера, особенности каждого кодека
        // и контейнера. Раньше одинаковый набор флагов подставлялся для всех — и для WebM/WMV/DVD получались ошибки ffmpeg.
        private static List<string> BuildArguments(string inputFile, string outputFile, VideoConversionOptions options, VideoExtraOptions? extra)
        {
            var ext = Path.GetExtension(outputFile).TrimStart('.').ToLowerInvariant();
            var container = FormatCatalog.GetVideoContainer(ext);

            var codec = (options.VideoCodec ?? container?.DefaultVideo ?? "libx264").ToLowerInvariant();
            // страховка: недопустимая пара «контейнер + кодек» заменяется кодеком по умолчанию для контейнера
            if (container != null && codec != "copy" && !container.VideoCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase))
                codec = container.DefaultVideo;

            var audioCodec = options.AudioCodec ?? container?.DefaultAudio ?? "aac";
            if (container != null && container.AudioCodecs.Length > 0 &&
                !container.AudioCodecs.Contains(audioCodec, StringComparer.OrdinalIgnoreCase))
                audioCodec = container.DefaultAudio;

            var args = new List<string> { "-y", "-i", inputFile, "-c:v", codec };

            var hasBitrate = options.VideoBitrate.HasValue && options.VideoBitrate.Value > 0;
            var preset = options.Preset ?? "medium";
            var info = FormatCatalog.GetVideoCodec(codec);

            if (codec != "copy")
            {
                switch (codec)
                {
                    case "libx264":
                    case "libx265":
                        if (hasBitrate) args.AddRange(new[] { "-b:v", $"{options.VideoBitrate!.Value}k" });
                        else if (options.Crf.HasValue) args.AddRange(new[] { "-crf", Math.Clamp(options.Crf.Value, 0, 51).ToString() });
                        args.AddRange(new[] { "-preset", preset, "-pix_fmt", "yuv420p" });   // yuv420p — чтобы файл играл везде (10-битные/4:4:4 исходники иначе не откроются)
                        if (codec == "libx265" && ext is "mp4" or "mov" or "m4v")
                            args.AddRange(new[] { "-tag:v", "hvc1" });                      // Apple и Windows опознают HEVC в mp4 только с этим тегом
                        break;

                    case "h264_nvenc":
                    case "hevc_nvenc":
                        if (hasBitrate)
                        {
                            var br = options.VideoBitrate!.Value;
                            args.AddRange(new[] { "-rc", "vbr", "-b:v", $"{br}k", "-maxrate:v", $"{br * 2}k" });
                        }
                        else
                        {
                            var cq = options.Crf switch { null => 23, <= 23 => 18, <= 28 => 23, var c => Math.Min(c.Value, 51) };
                            args.AddRange(new[] { "-rc", "vbr_hq", "-cq", cq.ToString() });
                        }
                        args.AddRange(new[] { "-preset", NvencPresets.GetValueOrDefault(preset, "p6"), "-pix_fmt", "yuv420p" });
                        if (codec == "hevc_nvenc" && ext is "mp4" or "mov" or "m4v") args.AddRange(new[] { "-tag:v", "hvc1" });
                        break;

                    case "libvpx-vp9":
                        if (hasBitrate) args.AddRange(new[] { "-b:v", $"{options.VideoBitrate!.Value}k" });
                        else args.AddRange(new[] { "-crf", Math.Clamp(options.Crf ?? 32, 0, 63).ToString(), "-b:v", "0" });
                        args.AddRange(new[] { "-row-mt", "1", "-cpu-used", "4", "-pix_fmt", "yuv420p" });
                        break;

                    case "libvpx":   // VP8: режим CRF требует верхнего ограничения битрейта
                        args.AddRange(new[] { "-crf", Math.Clamp(options.Crf ?? 10, 4, 63).ToString(),
                                              "-b:v", hasBitrate ? $"{options.VideoBitrate!.Value}k" : "5M", "-pix_fmt", "yuv420p" });
                        break;

                    case "libsvtav1":
                        if (hasBitrate) args.AddRange(new[] { "-b:v", $"{options.VideoBitrate!.Value}k" });
                        else args.AddRange(new[] { "-crf", Math.Clamp(options.Crf ?? 32, 0, 63).ToString() });
                        args.AddRange(new[] { "-preset", Svtav1Preset(preset), "-pix_fmt", "yuv420p" });
                        break;

                    case "prores_ks":
                        args.AddRange(new[] { "-profile:v", "3", "-pix_fmt", "yuv422p10le" });
                        break;

                    default:
                        if (info?.Quality == QualityKind.QScale)
                        {
                            // MPEG-4, WMV, FLV, MPEG-2, Theora: шкала качества (слайдер QScale на странице)
                            if (hasBitrate) args.AddRange(new[] { "-b:v", $"{options.VideoBitrate!.Value}k" });
                            else
                            {
                                var qs = Convert.ToInt32((object?)options.QScale);
                                var q = qs > 0 ? Math.Clamp(qs, 1, 31) : Math.Clamp((options.Crf ?? 23) / 2, 2, 31);
                                if (codec == "libtheora") q = Math.Clamp(10 - (q - 1) / 3, 0, 10);   // у Theora шкала 0–10, где 10 — лучшее
                                args.AddRange(new[] { "-q:v", q.ToString() });
                            }
                        }
                        else if (hasBitrate) args.AddRange(new[] { "-b:v", $"{options.VideoBitrate!.Value}k" });
                        break;
                }

                // фильтры: пикселизация + разрешение в одной цепочке
                var filters = new List<string>();
                if (extra is { PixelateFactor: >= 2 })
                {
                    var f = Math.Clamp(extra.PixelateFactor, 2, 16);
                    filters.Add($"scale=trunc(iw/{f}/2)*2:trunc(ih/{f}/2)*2");
                    filters.Add($"scale=trunc(iw*{f}/2)*2:trunc(ih*{f}/2)*2:flags=neighbor");
                }
                if (IsRealValue(options.Scale, "оригинал"))
                {
                    // кодеки с yuv420p требуют чётные размеры: "-1" → "-2"
                    var needEven = codec is "libx264" or "libx265" or "libvpx-vp9" or "libvpx" or "libsvtav1" or "mpeg2video" || codec.Contains("nvenc");
                    filters.Add($"scale={NormalizeScale(options.Scale!, needEven)}");
                }
                if (filters.Count > 0) args.AddRange(new[] { "-vf", string.Join(",", filters) });

                if (IsRealValue(options.Fps, "оригинальный"))
                    args.AddRange(new[] { "-r", options.Fps!.Trim() });
            }

            // ───── звук ─────
            if (container == null || container.AudioCodecs.Length > 0)
            {
                args.AddRange(new[] { "-c:a", audioCodec });
                if (audioCodec != "copy")
                {
                    var mono = extra is { AudioChannels: 1 };
                    if (options.AudioBitrate.HasValue && FormatCatalog.GetVideoAudioCodec(audioCodec) is { Lossy: true })
                        args.AddRange(new[] { "-b:a", $"{ClampAudioBitrate(audioCodec, options.AudioBitrate.Value, mono)}k" });
                    if (mono) args.AddRange(new[] { "-ac", "1" });
                    if (extra is { AudioSampleRate: > 0 })
                    {
                        var allowed = FormatCatalog.SampleRatesForVideoAudio(audioCodec);
                        var sr = allowed.Length == 0 ? extra.AudioSampleRate : allowed.OrderBy(r => Math.Abs(r - extra.AudioSampleRate)).First();
                        args.AddRange(new[] { "-ar", sr.ToString() });
                    }
                }
            }
            else
            {
                args.Add("-an");
            }

            if (container?.ExtraArgs != null) args.AddRange(container.ExtraArgs);

            args.Add(outputFile);
            return args;
        }

        /// <summary>Приводит битрейт к допустимому для аудиокодека (AC3/MP2 принимают только заданные значения, у AAC и Vorbis есть нижняя граница).</summary>
        private static int ClampAudioBitrate(string codec, int kbps, bool mono) => codec switch
        {
            "ac3" => FormatCatalog.NearestBitrate(FormatCatalog.GetAudioFormat("ac3")!, kbps),
            "mp2" => FormatCatalog.NearestBitrate(FormatCatalog.GetAudioFormat("mp2")!, kbps),
            "libvorbis" or "wmav2" => Math.Max(kbps, 32),
            "aac" => Math.Max(kbps, mono ? 8 : 16),
            _ => Math.Max(kbps, 6)
        };

        // SVT-AV1: preset 0 (медленно, лучше) … 13 (быстро)
        private static string Svtav1Preset(string speed) => speed.ToLowerInvariant() switch
        {
            "ultrafast" => "12", "superfast" => "11", "veryfast" => "10", "faster" => "9", "fast" => "8",
            "medium" => "7", "slow" => "5", "slower" => "4", "veryslow" => "3", _ => "7"
        };

        private static List<string> BuildGifArguments(string inputFile, string outputFile, VideoConversionOptions options)
        {
            var fps = IsRealValue(options.Fps, "оригинальный") ? options.Fps!.Trim() : "15";
            var scale = IsRealValue(options.Scale, "оригинал") ? NormalizeScale(options.Scale!, needEven: false) : "640:-1";

            var filter = $"fps={fps},scale={scale}:flags=lanczos,split[s0][s1];" +
                         "[s0]palettegen=max_colors=256[p];" +
                         "[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";

            return new List<string> { "-y", "-i", inputFile, "-vf", filter, outputFile };
        }

        /// <summary>"1280x720" → "1280:720" (иначе в GIF-фильтре с ":flags=…" значение не разбирается); "-1" → "-2" для кодеков с чётными размерами.</summary>
        private static string NormalizeScale(string scale, bool needEven)
        {
            var s = scale.Trim();
            var m = Regex.Match(s, @"^(\d+)x(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success) s = $"{m.Groups[1].Value}:{m.Groups[2].Value}";
            if (needEven) s = Regex.Replace(s, @"(?<![\d.])-1(?!\d)", "-2");
            return s;
        }

        private static bool IsRealValue(string? value, string originalWord)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            return !v.Equals(originalWord, StringComparison.OrdinalIgnoreCase)
                   && !v.Equals("original", StringComparison.OrdinalIgnoreCase);
        }
    }
}
