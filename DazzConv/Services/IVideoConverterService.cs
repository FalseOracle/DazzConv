using ConverToDazz.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DazzConv.Services
{
    /// <summary>Дополнительные параметры, которых нет в VideoConversionOptions: «шакал»-режим.</summary>
    public class VideoExtraOptions
    {
        /// <summary>0 — как в оригинале, 1 — моно.</summary>
        public int AudioChannels { get; set; }

        /// <summary>Частота дискретизации звука, Гц (0 — не менять). Например 8000, 11025, 22050.</summary>
        public int AudioSampleRate { get; set; }

        /// <summary>Пикселизация: картинка уменьшается в N раз и растягивается обратно (0 — выключено, 2…8).</summary>
        public int PixelateFactor { get; set; }
    }

    public interface IVideoConverterService
    {
        Task ConvertAsync(
            string inputFile,
            string outputFile,
            double durationSeconds,
            IProgress<int> progress,
            VideoConversionOptions options,
            CancellationToken token,
            VideoExtraOptions? extra = null);
    }
}
