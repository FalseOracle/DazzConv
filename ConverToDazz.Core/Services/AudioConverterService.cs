using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConverToDazz.Core.Services
{
    /// <summary>Параметры конвертации звука. Раньше сервис вообще их не принимал: битрейт, частота и нормализация
    /// из интерфейса игнорировались, и ffmpeg работал с настройками по умолчанию.</summary>
    public class AudioConversionOptions
    {
        public string? Codec { get; set; }          // libmp3lame, aac, flac …
        public int BitrateKbps { get; set; }        // 0 — не задавать
        public int SampleRate { get; set; }         // 0 — не менять
        public int Channels { get; set; }           // 0 — как в оригинале, 1 — моно
        public string? FormatExt { get; set; }      // формат из каталога (нужен, когда у двух форматов одинаковое расширение: ALAC и M4A)
        public bool Normalize { get; set; }         // loudnorm
        public bool StripVideo { get; set; }        // true для извлечения звука из видео
    }

    public class AudioConverterService
    {
        public async Task ConvertAsync(string ffmpegPath, string inputPath, string outputPath,
            TimeSpan duration, IProgress<int> progress, CancellationToken token = default,
            AudioConversionOptions? options = null)
        {
            if (!File.Exists(inputPath))
                throw new FileNotFoundException("Файл не найден", inputPath);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Аргументы строятся по КАТАЛОГУ форматов: у каждого формата свой кодек, допустимые битрейты и частоты,
            // максимум каналов и особенности контейнера. Раньше битрейт/частота вообще не передавались, а недопустимые
            // значения (например, AC3 на 100 kbps или OPUS на 22050 Гц) приводили к ошибке ffmpeg.
            var outExt = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
            var fmt = FormatCatalog.AudioFormats.FirstOrDefault(f => f.OutputExt.Equals(outExt, StringComparison.OrdinalIgnoreCase)
                                                                     && (options?.FormatExt == null || f.Ext.Equals(options.FormatExt, StringComparison.OrdinalIgnoreCase)))
                      ?? FormatCatalog.GetAudioFormat(outExt);

            var args = new List<string> { "-y", "-i", inputPath };

            // обложка альбома сохраняется только там, где контейнер её поддерживает
            var keepCover = options?.StripVideo != true && outExt is "mp3" or "m4a" or "flac";
            args.AddRange(new[] { "-map", "0:a:0" });
            if (keepCover) args.AddRange(new[] { "-map", "0:v:0?", "-c:v", "copy" });
            else args.Add("-vn");
            args.AddRange(new[] { "-map_metadata", "0" });

            var codec = options?.Codec ?? fmt?.Codec;
            if (!string.IsNullOrEmpty(codec)) args.AddRange(new[] { "-c:a", codec });

            if (fmt != null)
            {
                if (fmt.HasBitrate)
                {
                    var br = options is { BitrateKbps: > 0 } ? options.BitrateKbps : fmt.DefaultBitrate;
                    args.AddRange(new[] { "-b:a", $"{FormatCatalog.NearestBitrate(fmt, br)}k" });
                }

                // каналы: не больше, чем поддерживает формат (AMR — только моно)
                var channels = options is { Channels: > 0 } ? options.Channels : 0;
                if (fmt.MaxChannels == 1) channels = 1;
                else if (channels > fmt.MaxChannels) channels = fmt.MaxChannels;
                if (channels > 0) args.AddRange(new[] { "-ac", channels.ToString() });
                else if (fmt.MaxChannels == 2) args.AddRange(new[] { "-ac", "2" });          // многоканальный исходник → стерео для форматов без 5.1

                var sr = options is { SampleRate: > 0 } ? FormatCatalog.NearestSampleRate(fmt, options.SampleRate) : 0;
                if (fmt.SampleRates.Length == 1) sr = fmt.SampleRates[0];                    // AMR — только 8000 Гц
                else if (sr == 0 && fmt.Ext is "ac3" or "mp2" or "opus")                      // у этих форматов нет «любой» частоты
                    sr = FormatCatalog.NearestSampleRate(fmt, 44100);
                if (sr > 0) args.AddRange(new[] { "-ar", sr.ToString() });

                if (fmt.ExtraArgs != null) args.AddRange(fmt.ExtraArgs);
                if (fmt.Ext == "mp3") args.AddRange(new[] { "-id3v2_version", "3" });
            }
            else
            {
                if (options is { BitrateKbps: > 0 }) args.AddRange(new[] { "-b:a", $"{options.BitrateKbps}k" });
                if (options is { SampleRate: > 0 }) args.AddRange(new[] { "-ar", options.SampleRate.ToString() });
                if (options is { Channels: > 0 }) args.AddRange(new[] { "-ac", options.Channels.ToString() });
            }

            if (options?.Normalize == true) args.AddRange(new[] { "-af", "loudnorm" });
            args.AddRange(new[] { "-progress", "pipe:1", "-nostats", outputPath });
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            var errorTail = new Queue<string>();
            var lastPercent = -1;

            process.OutputDataReceived += (_, e) =>
            {
                // out_time_ms в выводе ffmpeg — на самом деле МИКРОСЕКУНДЫ (раньше делили на миллисекунды длительности,
                // и прогресс мгновенно прыгал на 100%)
                var line = e.Data;
                if (line == null || duration.TotalMilliseconds <= 0) return;
                if (!line.StartsWith("out_time_us=") && !line.StartsWith("out_time_ms=")) return;
                if (!long.TryParse(line[(line.IndexOf('=') + 1)..], out var micro) || micro < 0) return;

                var percent = (int)Math.Clamp(micro / 1000.0 / duration.TotalMilliseconds * 100, 0, 99);
                if (percent != lastPercent) { lastPercent = percent; progress?.Report(percent); }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (errorTail) { errorTail.Enqueue(e.Data); if (errorTail.Count > 20) errorTail.Dequeue(); }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // отмена реально останавливает ffmpeg (раньше процесс продолжал работать в фоне)
            using var reg = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                throw;
            }

            // раньше код возврата не проверялся: при ошибке ffmpeg приложение говорило «готово»
            if (process.ExitCode != 0)
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                string tail;
                lock (errorTail) tail = string.Join("\n", errorTail.TakeLast(3));
                throw new Exception($"FFmpeg ошибка (код {process.ExitCode}):\n{tail}");
            }

            progress?.Report(100);
        }

        public string[] SupportedExtensions => InputFormats.Audio;
    }
}
