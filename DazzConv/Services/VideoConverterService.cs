using ConverToDazz.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DazzConv.Services
{
    /// <summary>
    /// Совместимая обёртка для BatchConverter.razor (старая сигнатура).
    /// Вся логика теперь в <see cref="FfmpegVideoConverterService"/> — дублирование кода убрано.
    /// </summary>
    public class VideoConverterService
    {
        public Task ConvertAsync(
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
            var isGif = string.Equals(videoCodec, "gif", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(outputFormat, "gif", StringComparison.OrdinalIgnoreCase);

            var options = new VideoConversionOptions
            {
                VideoCodec = isGif ? "gif" : videoCodec,
                AudioCodec = "aac",
                Crf = crf,
                Preset = preset,
                Scale = resolution,
                Fps = fps,
                AudioBitrate = audioBitrate
            };

            // durationSeconds = 0 → длительность будет прочитана из вывода ffmpeg
            return FfmpegVideoConverterService.ConvertAsync(
                ffmpegPath, inputFile, outputFile, 0, progress, options, token);
        }
    }
}
