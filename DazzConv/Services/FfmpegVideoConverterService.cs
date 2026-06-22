using ConverToDazz.Core.Models;
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
    public class FfmpegVideoConverterService : IVideoConverterService
    {
        private readonly FFmpegPathService _ffmpegService;

        public FfmpegVideoConverterService(FFmpegPathService ffmpegService)
        {
            _ffmpegService = ffmpegService;
        }

        public async Task ConvertAsync(
            string inputFile,
            string outputFile,
            double durationSeconds,
            IProgress<int> progress,
            VideoConversionOptions options,
            CancellationToken token)
        {
            var ffmpegPath = _ffmpegService.GetFFmpegPath();

            if (!File.Exists(ffmpegPath))
                throw new FileNotFoundException($"FFmpeg не найден: {ffmpegPath}");

            if (!File.Exists(inputFile))
                throw new FileNotFoundException($"Файл не найден: {inputFile}");

            var outputDir = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            var args = BuildArguments(inputFile, outputFile, options);

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

            var duration = TimeSpan.FromSeconds(durationSeconds);
            var errorOutput = "";

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data == null) return;
                errorOutput += e.Data + "\n";

                if (duration > TimeSpan.Zero)
                {
                    var timeMatch = Regex.Match(e.Data, @"time=(\d+):(\d+):(\d+\.\d+)");
                    if (timeMatch.Success)
                    {
                        var hours = int.Parse(timeMatch.Groups[1].Value);
                        var minutes = int.Parse(timeMatch.Groups[2].Value);
                        var seconds = double.Parse(timeMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                        var currentTime = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);

                        var percent = (int)(currentTime.TotalSeconds / duration.TotalSeconds * 100);
                        progress.Report(Math.Min(100, Math.Max(0, percent)));
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

            progress.Report(100);
        }

        private string BuildArguments(string inputFile, string outputFile, VideoConversionOptions options)
        {
            var args = "-y ";
            args += $"-i \"{inputFile}\" ";

            // Определяем реальный кодек
            string actualCodec = (options.VideoCodec ?? "libx264").ToLower();

            args += $"-c:v {actualCodec} ";

            // Параметры качества
            if (actualCodec.Contains("nvenc"))
            {
                // Приоритет битрейту для NVENC
                if (options.VideoBitrate.HasValue && options.VideoBitrate.Value > 0)
                {
                    args += $"-rc vbr -b:v {options.VideoBitrate.Value}k ";
                    args += $"-maxrate:v {options.VideoBitrate.Value * 2}k ";
                }
                else if (options.Crf.HasValue)
                {
                    var crf = options.Crf.Value;
                    // УБРАЛИ lossless
                    if (crf <= 18)
                        args += "-rc vbr_hq -cq 18 ";
                    else if (crf <= 23)
                        args += "-rc vbr_hq -cq 18 ";
                    else if (crf <= 28)
                        args += "-rc vbr_hq -cq 23 ";
                    else
                        args += $"-rc vbr_hq -cq {crf} ";
                }
                else
                {
                    args += "-rc vbr_hq -cq 23 ";
                }

                var preset = options.Preset ?? "medium";
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
                // Приоритет битрейту
                if (options.VideoBitrate.HasValue && options.VideoBitrate.Value > 0)
                {
                    args += $"-b:v {options.VideoBitrate.Value}k ";
                }
                else if (options.Crf.HasValue)
                {
                    args += $"-crf {options.Crf.Value} ";
                }

                args += $"-preset {options.Preset ?? "medium"} ";
            }
            else if (actualCodec == "mpeg4")
            {
                var qscale = Math.Max(2, Math.Min(31, (options.Crf ?? 23) / 2));
                args += $"-q:v {qscale} ";
            }

            else if (actualCodec == "libx264" || actualCodec == "libx265")
            {
                args += $"-crf {options.Crf ?? 23} ";
                args += $"-preset {options.Preset ?? "medium"} ";
            }
            else if (actualCodec == "mpeg4")
            {
                var qscale = Math.Max(2, Math.Min(31, (options.Crf ?? 23) / 2));
                args += $"-q:v {qscale} ";
            }

            // Разрешение
            if (!string.IsNullOrEmpty(options.Scale))
            {
                var cleanScale = options.Scale.Trim().ToLower();
                if (!string.IsNullOrEmpty(cleanScale) &&
                    cleanScale != "оригинал" &&
                    cleanScale != "original")
                {
                    args += $"-vf scale={options.Scale} ";
                }
            }

            // FPS
            if (!string.IsNullOrEmpty(options.Fps))
            {
                var cleanFps = options.Fps.Trim().ToLower();
                if (!string.IsNullOrEmpty(cleanFps) &&
                    cleanFps != "оригинальный" &&
                    cleanFps != "original")
                {
                    args += $"-r {options.Fps} ";
                }
            }

            // Видео битрейт (если указан - переопределяет CRF)
            if (options.VideoBitrate.HasValue && options.VideoBitrate.Value > 0)
            {
                args += $"-b:v {options.VideoBitrate.Value}k ";
            }

            // Аудио
            var audioCodec = options.AudioCodec ?? "aac";
            args += $"-c:a {audioCodec} ";

            if (audioCodec != "copy" && options.AudioBitrate.HasValue)
            {
                args += $"-b:a {options.AudioBitrate.Value}k ";
            }

            args += $"\"{outputFile}\"";
            return args;
        }
    }
}