using ConverToDazz.Core.Models;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DazzConv.Services
{
    public class VideoInfoService
    {
        private readonly FFmpegPathService _ffmpegService;

        public VideoInfoService(FFmpegPathService ffmpegService)
        {
            _ffmpegService = ffmpegService;
        }

        public async Task<VideoInfo> GetInfoAsync(string filePath)
        {
            var ffmpegPath = _ffmpegService.GetFFmpegPath();
            var info = new VideoInfo();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-i \"{filePath}\"",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var errorOutput = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                // Парсим длительность
                var durationMatch = Regex.Match(errorOutput, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
                if (durationMatch.Success)
                {
                    var hours = int.Parse(durationMatch.Groups[1].Value);
                    var minutes = int.Parse(durationMatch.Groups[2].Value);
                    var seconds = double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                    var duration = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
                    info.Duration = $"{hours:D2}:{minutes:D2}:{(int)seconds:D2}";
                    info.DurationSeconds = duration.TotalSeconds;
                }

                // Парсим видео поток
                var codecMatch = Regex.Match(errorOutput, @"Video:\s*(\S+)");
                if (codecMatch.Success)
                    info.Codec = codecMatch.Groups[1].Value.TrimEnd(',');

                var resMatch = Regex.Match(errorOutput, @"(\d{3,5}x\d{3,5})");
                if (resMatch.Success)
                    info.Resolution = resMatch.Groups[1].Value;

                var fpsMatch = Regex.Match(errorOutput, @"(\d+(?:\.\d+)?)\s*fps");
                if (fpsMatch.Success)
                    info.Fps = fpsMatch.Groups[1].Value;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка получения информации: {ex.Message}");
            }

            return info;
        }
    }
}