using System;
using System.Collections.Generic;

namespace ConverToDazz.Core.Models
{
    public class VideoInfo
    {
        public string Duration { get; set; } = string.Empty;
        public string Codec { get; set; } = string.Empty;
        public string Resolution { get; set; } = string.Empty;
        public string Fps { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
    }

    public class VideoConversionOptions
    {
        public string? VideoCodec { get; set; }
        public string? AudioCodec { get; set; }
        public int? VideoBitrate { get; set; }
        public int? AudioBitrate { get; set; }
        public int? Crf { get; set; }
        public int? QScale { get; set; }
        public string? Preset { get; set; }
        public string? HwAccel { get; set; }
        public string? Scale { get; set; }
        public string? Fps { get; set; }
    }

    public class VideoProfile
    {
        public string Name { get; set; } = string.Empty;
        public string? VideoCodec { get; set; }
        public string? AudioCodec { get; set; }
        public int? VideoBitrate { get; set; }
        public int? AudioBitrate { get; set; }
        public int? Crf { get; set; }
        public int? QScale { get; set; }
        public string? Preset { get; set; }
        public string? HwAccel { get; set; }
        public string? Scale { get; set; }
        public string? Fps { get; set; }
    }
}