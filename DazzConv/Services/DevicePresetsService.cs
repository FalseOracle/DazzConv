using System.Collections.Generic;
using System.Linq;

namespace DazzConv.Services
{
    public class DevicePreset
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "";
        public string Category { get; set; } = "";

        public string? VideoCodec { get; set; }
        public string? VideoFormat { get; set; }
        public string? Resolution { get; set; }
        public int? VideoBitrate { get; set; }
        public string? Fps { get; set; }
        public int? Crf { get; set; }

        public string? AudioCodec { get; set; }
        public int? AudioBitrate { get; set; }
        public int? SampleRate { get; set; }

        public string? ImageFormat { get; set; }
        public int? ImageQuality { get; set; }
        public int? ImageWidth { get; set; }
        public int? ImageHeight { get; set; }

        public string Description { get; set; } = "";
    }

    public static class DevicePresets
    {
        public static List<DevicePreset> GetAll()
        {
            return new List<DevicePreset>
            {
                new DevicePreset
                {
                    Id = "iphone", Name = "iPhone / iOS", Icon = "", Category = "phone",
                    VideoCodec = "libx264", VideoFormat = "mp4", Resolution = "1080p",
                    VideoBitrate = 5000, AudioCodec = "aac", AudioBitrate = 192, Fps = "30",
                    Description = "H.264 + AAC, совместимо со всеми iOS устройствами"
                },
                new DevicePreset
                {
                    Id = "android", Name = "Android (универсальный)", Icon = "🤖", Category = "phone",
                    VideoCodec = "libx264", VideoFormat = "mp4", Resolution = "1080p",
                    VideoBitrate = 4000, AudioCodec = "aac", AudioBitrate = 192,
                    Description = "H.264 + AAC, работает на большинстве Android"
                },
                new DevicePreset
                {
                    Id = "tiktok", Name = "TikTok / Reels / Shorts", Icon = "🎵", Category = "social",
                    VideoCodec = "libx264", VideoFormat = "mp4", Resolution = "1080p",
                    VideoBitrate = 8000, AudioCodec = "aac", AudioBitrate = 192, Fps = "60",
                    Description = "Вертикальное видео 9:16, 1080x1920, 60fps"
                },
                new DevicePreset
                {
                    Id = "youtube", Name = "YouTube (рекомендуемое)", Icon = "▶️", Category = "social",
                    VideoCodec = "libx264", VideoFormat = "mp4", Resolution = "1080p",
                    VideoBitrate = 8000, AudioCodec = "aac", AudioBitrate = 192, Crf = 23,
                    Description = "Оптимальное качество для YouTube"
                },
                new DevicePreset
                {
                    Id = "discord", Name = "Discord", Icon = "", Category = "social",
                    VideoCodec = "libx264", VideoFormat = "mp4", Resolution = "720p",
                    VideoBitrate = 5000, AudioCodec = "aac", AudioBitrate = 128,
                    Description = "До 8MB для Nitro, до 25MB для Nitro Boost"
                },
                new DevicePreset
                {
                    Id = "whatsapp", Name = "WhatsApp", Icon = "💬", Category = "social",
                    VideoCodec = "libx264", VideoFormat = "mp4", Resolution = "720p",
                    VideoBitrate = 2000, AudioCodec = "aac", AudioBitrate = 128,
                    Description = "Сжатие до 16MB для отправки в WhatsApp"
                },
                new DevicePreset
                {
                    Id = "car_audio", Name = "Автомагнитола", Icon = "🚗", Category = "audio",
                    AudioCodec = "mp3", AudioBitrate = 320, SampleRate = 44100,
                    Description = "MP3 320kbps — максимальная совместимость"
                },
                new DevicePreset
                {
                    Id = "podcast", Name = "Подкаст", Icon = "🎙️", Category = "audio",
                    AudioCodec = "mp3", AudioBitrate = 128, SampleRate = 44100,
                    Description = "MP3 128kbps моно — стандарт для подкастов"
                },
                new DevicePreset
                {
                    Id = "music_hq", Name = "Музыка (HQ)", Icon = "🎧", Category = "audio",
                    AudioCodec = "flac", SampleRate = 48000,
                    Description = "FLAC без потерь — для аудиофилов"
                },
                new DevicePreset
                {
                    Id = "instagram_post", Name = "Instagram пост", Icon = "📷", Category = "image",
                    ImageFormat = "jpg", ImageQuality = 90, ImageWidth = 1080, ImageHeight = 1080,
                    Description = "Квадрат 1080x1080, JPG 90%"
                },
                new DevicePreset
                {
                    Id = "web_optimized", Name = "Для сайта", Icon = "", Category = "image",
                    ImageFormat = "webp", ImageQuality = 80,
                    Description = "WebP 80% — оптимально для веба"
                },
            };
        }

        public static List<DevicePreset> GetByCategory(string category)
        {
            return GetAll().Where(p => p.Category == category).ToList();
        }
    }
}