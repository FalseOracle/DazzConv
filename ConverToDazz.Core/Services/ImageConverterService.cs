using ImageMagick;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DazzConv.Services
{
    public class ImageConversionOptions
    {
        public string OutputFormat { get; set; } = "png";
        public int Quality { get; set; } = 85;
        public bool ResizeEnabled { get; set; } = false;
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public bool KeepAspectRatio { get; set; } = true;
        public bool OptimizeSize { get; set; } = false;
    }

    public class ImageConverterService
    {
        public string[] SupportedExtensions => new[]
        {
            "jpg", "jpeg", "png", "gif", "bmp",
            "webp", "avif", "tiff", "ico"
        };

        public async Task<byte[]> ConvertAdvancedAsync(string inputPath, ImageConversionOptions options)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var image = new MagickImage(inputPath);

                    // Валидация размеров
                    if (options.ResizeEnabled)
                    {
                        if (options.Width < 1 || options.Width > 8192 ||
                            options.Height < 1 || options.Height > 8192)
                        {
                            throw new ArgumentException(
                                "Недопустимый размер изображения. Допустимый диапазон: 1-8192 пикселей");
                        }

                        var totalPixels = (long)options.Width * options.Height;
                        if (totalPixels > 67_108_864)
                        {
                            throw new ArgumentException(
                                "Слишком большое изображение. Максимум: 8192x8192 пикселей");
                        }
                    }

                    if (options.OptimizeSize)
                    {
                        image.Strip();
                    }

                    var format = GetMagickFormat(options.OutputFormat);

                    if (format == MagickFormat.Icon)
                    {
                        return ConvertToIcon(image, options.Quality);
                    }

                    if (options.ResizeEnabled)
                    {
                        if (options.KeepAspectRatio)
                        {
                            image.Resize(new MagickGeometry((uint)options.Width, (uint)options.Height)
                            {
                                IgnoreAspectRatio = false
                            });
                        }
                        else
                        {
                            image.Resize((uint)options.Width, (uint)options.Height);
                        }
                    }

                    image.Quality = (uint)options.Quality;

                    if (format == MagickFormat.Avif || format == MagickFormat.Heic)
                    {
                        return ConvertWithFallback(image, format, options.Quality);
                    }

                    using var ms = new MemoryStream();
                    image.Format = format;
                    image.Write(ms);
                    return ms.ToArray();
                }
                catch (MagickCoderErrorException ex) when (
                    ex.Message.Contains("iCCP", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase))
                {
                    // НОВОЕ: Автоматический retry с удалением профилей
                    System.Diagnostics.Debug.WriteLine($"[ImageConverter] iCCP/profile ошибка, retry с Strip: {ex.Message}");

                    var settings = new MagickReadSettings
                    {
                        ColorSpace = ColorSpace.sRGB
                    };

                    using var image = new MagickImage(inputPath, settings);
                    image.Strip(); // Удаляем ВСЕ профили
                    image.ColorSpace = ColorSpace.sRGB;

                    var format = GetMagickFormat(options.OutputFormat);

                    if (options.ResizeEnabled)
                    {
                        if (options.KeepAspectRatio)
                        {
                            image.Resize(new MagickGeometry((uint)options.Width, (uint)options.Height)
                            {
                                IgnoreAspectRatio = false
                            });
                        }
                        else
                        {
                            image.Resize((uint)options.Width, (uint)options.Height);
                        }
                    }

                    image.Quality = (uint)options.Quality;

                    if (format == MagickFormat.Icon)
                    {
                        return ConvertToIcon(image, options.Quality);
                    }

                    using var ms = new MemoryStream();
                    image.Format = format;
                    image.Write(ms);
                    return ms.ToArray();
                }
            });
        }

        // НОВОЕ: Конвертация с принудительным удалением всех профилей (fallback для iCCP ошибок)
        public async Task<byte[]> ConvertAdvancedWithStripProfile(string inputPath, ImageConversionOptions options)
        {
            return await Task.Run(() =>
            {
                // Читаем с явным указанием sRGB, чтобы избежать проблем с профилями
                var settings = new MagickReadSettings
                {
                    ColorSpace = ColorSpace.sRGB
                };

                using var image = new MagickImage(inputPath, settings);

                // Удаляем ВСЕ профили (включая повреждённый iCCP)
                image.Strip();
                image.ColorSpace = ColorSpace.sRGB;

                // Валидация размеров
                if (options.ResizeEnabled)
                {
                    if (options.Width < 1 || options.Width > 8192 ||
                        options.Height < 1 || options.Height > 8192)
                    {
                        throw new ArgumentException("Недопустимый размер изображения. Допустимый диапазон: 1-8192 пикселей");
                    }
                }

                // Ресайз
                if (options.ResizeEnabled)
                {
                    if (options.KeepAspectRatio)
                    {
                        image.Resize(new MagickGeometry((uint)options.Width, (uint)options.Height)
                        {
                            IgnoreAspectRatio = false
                        });
                    }
                    else
                    {
                        image.Resize((uint)options.Width, (uint)options.Height);
                    }
                }

                image.Quality = (uint)options.Quality;

                var format = GetMagickFormat(options.OutputFormat);

                // Для ICO
                if (format == MagickFormat.Icon)
                {
                    return ConvertToIcon(image, options.Quality);
                }

                // Для AVIF/HEIC
                if (format == MagickFormat.Avif || format == MagickFormat.Heic)
                {
                    return ConvertWithFallback(image, format, options.Quality);
                }

                using var ms = new MemoryStream();
                image.Format = format;
                image.Write(ms);
                return ms.ToArray();
            });
        }

        // Специальный метод для конвертации в ICO
        private byte[] ConvertToIcon(MagickImage image, int quality)
        {
            // ICO поддерживает максимум 256x256
            const int maxIconSize = 256;

            // Если изображение больше 256x256 - уменьшаем
            if (image.Width > maxIconSize || image.Height > maxIconSize)
            {
                // Используем Lanczos для лучшего качества при уменьшении
                image.FilterType = FilterType.Lanczos;

                // Уменьшаем с сохранением пропорций
                image.Resize(new MagickGeometry((uint)maxIconSize, (uint)maxIconSize)
                {
                    IgnoreAspectRatio = false
                });
            }

            // Устанавливаем формат ICO
            image.Format = MagickFormat.Icon;
            image.Quality = (uint)Math.Min(quality, 95);

            try
            {
                using var ms = new MemoryStream();
                image.Write(ms);
                return ms.ToArray();
            }
            catch (MagickException ex)
            {
                // Если всё равно не работает - пробуем с PNG внутри ICO
                try
                {
                    image.Format = MagickFormat.Png;
                    image.Quality = 95;

                    using var pngMs = new MemoryStream();
                    image.Write(pngMs);
                    var pngData = pngMs.ToArray();

                    // Создаём новый ICO из PNG
                    using var iconImage = new MagickImage(pngData);
                    iconImage.Format = MagickFormat.Icon;

                    using var iconMs = new MemoryStream();
                    iconImage.Write(iconMs);
                    return iconMs.ToArray();
                }
                catch (Exception fallbackEx)
                {
                    throw new InvalidOperationException(
                        $"Не удалось создать ICO файл. Убедитесь что изображение не повреждено. " +
                        $"Исходная ошибка: {ex.Message}", fallbackEx);
                }
            }
        }

        // Создание превью
        public async Task<byte[]> CreatePreviewAsync(string inputPath, int maxSize = 300)
        {
            return await Task.Run(() =>
            {
                using var image = new MagickImage(inputPath);

                image.Resize(new MagickGeometry((uint)maxSize, (uint)maxSize)
                {
                    IgnoreAspectRatio = false
                });

                image.Format = MagickFormat.Png;
                image.Quality = 85;

                using var ms = new MemoryStream();
                image.Write(ms);
                return ms.ToArray();
            });
        }

        private byte[] ConvertWithFallback(MagickImage image, MagickFormat format, int quality)
        {
            image.Quality = (uint)Math.Min(quality, 95);
            image.Format = format;

            try
            {
                using var ms = new MemoryStream();
                image.Write(ms);
                return ms.ToArray();
            }
            catch (MagickException)
            {
                try
                {
                    image.Quality = 85;
                    using var ms = new MemoryStream();
                    image.Write(ms);
                    return ms.ToArray();
                }
                catch (MagickException)
                {
                    image.Format = MagickFormat.WebP;
                    image.Quality = (uint)Math.Min(quality, 90);

                    using var ms = new MemoryStream();
                    image.Write(ms);
                    return ms.ToArray();
                }
            }
        }

        private MagickFormat GetMagickFormat(string extension)
        {
            return extension.ToLower() switch
            {
                "png" => MagickFormat.Png,
                "jpg" or "jpeg" => MagickFormat.Jpeg,
                "webp" => MagickFormat.WebP,
                "avif" => MagickFormat.Avif,
                "heic" => MagickFormat.Heic,
                "bmp" => MagickFormat.Bmp,
                "tiff" or "tif" => MagickFormat.Tiff,
                "gif" => MagickFormat.Gif,
                "ico" => MagickFormat.Icon,
                _ => MagickFormat.Unknown
            };
        }

        public byte[] Convert(MagickImage original, string targetFormat)
        {
            using var ms = new MemoryStream();
            original.Format = GetMagickFormat(targetFormat);
            original.Write(ms);
            return ms.ToArray();
        }
    }
}