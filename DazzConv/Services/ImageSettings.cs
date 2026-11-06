using ConverToDazz.Core.Services;
using System;

namespace DazzConv.Services
{
    /// <summary>
    /// Все настройки конвертации изображений в одном месте (страница «Изображения» и «Пакетная конвертация»
    /// используют общую форму ImageSettingsForm). Особенности форматов берутся из FormatCatalog.
    /// </summary>
    public class ImageSettings
    {
        public string OutputFormat { get; private set; } = "png";
        public int Quality { get; set; } = 85;
        public bool ResizeEnabled { get; set; }
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public bool KeepAspectRatio { get; set; } = true;
        public bool OptimizeSize { get; set; }

        public ImageFormatInfo Format => FormatCatalog.GetImageFormat(OutputFormat) ?? FormatCatalog.ImageFormats[1];

        public bool ShowQuality => Format.SupportsQuality;

        public void SetFormat(string ext)
        {
            var f = FormatCatalog.GetImageFormat(ext);
            if (f == null) return;
            OutputFormat = f.Ext;
        }

        public static bool IsValidSize(int size) => size >= 1 && size <= 8192;

        public string? Validate()
        {
            if (ResizeEnabled && (!IsValidSize(Width) || !IsValidSize(Height)))
                return "❌ Размер изображения: от 1 до 8192 пикселей";
            return null;
        }

        public ImageConversionOptions ToOptions() => new()
        {
            OutputFormat = OutputFormat,
            Quality = Quality,
            ResizeEnabled = ResizeEnabled,
            Width = Width,
            Height = Height,
            KeepAspectRatio = KeepAspectRatio,
            OptimizeSize = OptimizeSize
        };

        public ImageSettings Clone() => (ImageSettings)MemberwiseClone();

        public static string QualityDescription(int q) =>
            q >= 95 ? "🌟 Максимальное" : q >= 85 ? "⭐ Отличное" : q >= 70 ? "✅ Хорошее" : q >= 50 ? "👍 Среднее" : q >= 30 ? "📱 Низкое" : "📉 Минимальное";
    }
}
