using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DazzConv.Services
{
    public class VideoConverterService
    {
        public async Task ConvertAsync(
            string ffmpegPath,
            string inputFile,
            string outputFile,
            string outputFormat = "mp4",
            string videoCodec = "libx264",
            int crf = 23,
            string preset = "medium",
            string resolution = "",
            int audioBitrate = 192,
            string? hwAccel = null,
            string? fps = null,
            IProgress<int>? progress = null,
            CancellationToken token = default)
        {
            if (!File.Exists(ffmpegPath))
                throw new FileNotFoundException($"FFmpeg не найден: {ffmpegPath}");

            if (!File.Exists(inputFile))
                throw new FileNotFoundException($"Входной файл не найден: {inputFile}");

            var outputDir = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            var args = BuildFFmpegArguments(
                inputFile, outputFile, outputFormat,
                videoCodec, crf, preset, resolution, audioBitrate, hwAccel, fps);

            try
            {
                File.WriteAllText("last_ffmpeg_args.txt", args);
            }
            catch { }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };

            var duration = TimeSpan.Zero;
            var errorOutput = "";

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                errorOutput += e.Data + "\n";

                var durationMatch = Regex.Match(e.Data, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
                if (durationMatch.Success && duration == TimeSpan.Zero)
                {
                    var hours = int.Parse(durationMatch.Groups[1].Value);
                    var minutes = int.Parse(durationMatch.Groups[2].Value);
                    var seconds = double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                    duration = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
                }

                if (duration > TimeSpan.Zero)
                {
                    var timeMatch = Regex.Match(e.Data, @"time=(\d+):(\d+):(\d+\.\d+)");
                    if (timeMatch.Success)
                    {
                        var hours = int.Parse(timeMatch.Groups[1].Value);
                        var minutes = int.Parse(timeMatch.Groups[2].Value);
                        var seconds = double.Parse(timeMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                        var currentTime = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);

                        // Защита от деления на ноль и переполнения
                        if (duration.TotalSeconds > 0)
                        {
                            var percent = (int)(currentTime.TotalSeconds / duration.TotalSeconds * 100);
                            progress?.Report(Math.Min(100, Math.Max(0, percent)));
                        }
                    }
                }
            };

            process.Start();
            process.BeginErrorReadLine();

            await Task.Run(() => process.WaitForExit(), token);

            if (process.ExitCode != 0)
            {
                var lastError = errorOutput.Split('\n').TakeLast(5).FirstOrDefault() ?? "Неизвестная ошибка";
                throw new Exception($"FFmpeg ошибка (код {process.ExitCode}):\n{lastError}");
            }

            if (!File.Exists(outputFile))
                throw new Exception($"Выходной файл не создан: {outputFile}");

            progress?.Report(100);
        }

        private string BuildFFmpegArguments(
            string inputFile, string outputFile, string outputFormat,
            string videoCodec, int crf, string preset, string resolution,
            int audioBitrate, string? hwAccel, string? fps)
        {
            var args = "-y ";
            args += $"-i \"{inputFile}\" ";

            // Определяем реальный кодек (с учётом NVENC)
            string actualCodec = videoCodec.ToLower();

            // Видео кодек
            args += $"-c:v {actualCodec} ";

            // Параметры качества в зависимости от кодека
            if (actualCodec.Contains("nvenc"))
            {
                // Для NVENC - используем CQ (аналог CRF)
                // УБРАЛИ lossless - не все GPU поддерживают
                if (crf <= 18)
                    args += "-rc vbr_hq -cq 18 ";  // Вместо lossless
                else if (crf <= 23)
                    args += "-rc vbr_hq -cq 18 ";
                else if (crf <= 28)
                    args += "-rc vbr_hq -cq 23 ";
                else
                    args += $"-rc vbr_hq -cq {crf} ";

                // Preset для NVENC
                args += preset switch
                {
                    "ultrafast" => "-preset p1 ",
                    "superfast" => "-preset p2 ",
                    "veryfast" => "-preset p3 ",
                    "faster" => "-preset p4 ",
                    "fast" => "-preset p5 ",
                    "medium" => "-preset p6 ",
                    "slow" => "-preset p7 ",
                    "slower" => "-preset p7 ",
                    "veryslow" => "-preset p7 ",
                    _ => "-preset p6 "
                };
            }
            else if (actualCodec == "libx264" || actualCodec == "libx265")
            {
                args += $"-crf {crf} ";
                args += $"-preset {preset} ";
            }
            else if (actualCodec == "mpeg4")
            {
                var qscale = Math.Max(2, Math.Min(31, crf / 2));
                args += $"-q:v {qscale} ";
            }

            // Разрешение
            if (!string.IsNullOrEmpty(resolution))
            {
                var cleanResolution = resolution.Trim().ToLower();
                if (!string.IsNullOrEmpty(cleanResolution) &&
                    cleanResolution != "оригинал" &&
                    cleanResolution != "original")
                {
                    args += $"-vf scale={resolution} ";
                }
            }

            // FPS
            if (!string.IsNullOrEmpty(fps))
            {
                var cleanFps = fps.Trim().ToLower();
                if (!string.IsNullOrEmpty(cleanFps) &&
                    cleanFps != "оригинальный" &&
                    cleanFps != "original")
                {
                    args += $"-r {fps} ";
                }
            }

            // Аудио
            args += $"-c:a aac -b:a {audioBitrate}k ";

            // Формат
            if (!string.IsNullOrEmpty(outputFormat) && outputFormat.ToLower() != "mp4")
            {
                args += $"-f {outputFormat} ";
            }

            args += $"\"{outputFile}\"";
            return args;
        }
    }
}