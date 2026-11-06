using ConverToDazz.Core.Services;
using ImageMagick;
using System;
using System.IO;
using System.Threading;
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
        public string[] SupportedExtensions => InputFormats.Image;

        public async Task<byte[]> ConvertAdvancedAsync(string inputPath, ImageConversionOptions options, CancellationToken token = default)
        {
            return await Task.Run(() =>
            {
                try
                {
                    return ConvertCore(inputPath, options, stripProfiles: false, token);
                }
                catch (MagickCoderErrorException ex) when (
                    ex.Message.Contains("iCCP", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase))
                {
                    // повреждённый ICC-профиль: повторяем с удалением всех профилей
                    System.Diagnostics.Debug.WriteLine($"[ImageConverter] iCCP/profile ошибка, retry с Strip: {ex.Message}");
                    return ConvertCore(inputPath, options, stripProfiles: true, token);
                }
            }, token);
        }

        // Fallback для iCCP-ошибок (оставлен для совместимости с BatchConverter)
        public async Task<byte[]> ConvertAdvancedWithStripProfile(string inputPath, ImageConversionOptions options, CancellationToken token = default)
        {
            return await Task.Run(() => ConvertCore(inputPath, options, stripProfiles: true, token), token);
        }

        // Раньше этот код был скопирован ТРИ раза (основной путь, retry в catch и ConvertAdvancedWithStripProfile)
        private byte[] ConvertCore(string inputPath, ImageConversionOptions options, bool stripProfiles, CancellationToken token)
        {
            ValidateOptions(options);
            token.ThrowIfCancellationRequested();

            using var image = stripProfiles
                ? new MagickImage(inputPath, new MagickReadSettings { ColorSpace = ColorSpace.sRGB })
                : new MagickImage(inputPath);

            if (stripProfiles)
            {
                image.Strip();                      // удаляем ВСЕ профили, включая повреждённый iCCP
                image.ColorSpace = ColorSpace.sRGB;
            }
            else if (options.OptimizeSize)
            {
                image.Strip();
            }

            token.ThrowIfCancellationRequested();
            var format = GetMagickFormat(options.OutputFormat);

            // анимированные GIF/WebP: сохраняем все кадры (раньше читался только первый)
            if (!stripProfiles && (format == MagickFormat.Gif || format == MagickFormat.WebP))
            {
                var animated = TryConvertAnimated(inputPath, options, format, token);
                if (animated != null) return animated;
            }

            // БАГ: для "ico" GetMagickFormat возвращает MagickFormat.Ico, а проверялось только MagickFormat.Icon —
            // специальная ветка ICO (ограничение 256x256) никогда не срабатывала.
            if (IsIcon(format))
                return ConvertToIcon(image, options.Quality);

            if (options.ResizeEnabled)
            {
                // БАГ: Resize(width, height) всегда сохраняет пропорции, поэтому «не сохранять пропорции» не работало
                image.Resize(new MagickGeometry((uint)options.Width, (uint)options.Height)
                {
                    IgnoreAspectRatio = !options.KeepAspectRatio
                });
            }

            // формат без прозрачности (JPEG, BMP, …): подкладываем белый фон — раньше прозрачные области становились чёрными
            var catalogInfo = FormatCatalog.GetImageFormat(options.OutputFormat);
            if (catalogInfo != null && !catalogInfo.Alpha && image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }

            token.ThrowIfCancellationRequested();
            image.Quality = (uint)options.Quality;

            if (format == MagickFormat.Avif || format == MagickFormat.Heic)
                return ConvertWithFallback(image, format, options.Quality);

            using var ms = new MemoryStream();
            image.Format = format;
            image.Write(ms);
            return ms.ToArray();
        }

        /// <summary>Многокадровый GIF/WebP → тот же формат со всеми кадрами. Возвращает null, если файл одно-кадровый.</summary>
        private byte[]? TryConvertAnimated(string inputPath, ImageConversionOptions options, MagickFormat format, CancellationToken token)
        {
            try
            {
                using var collection = new MagickImageCollection(inputPath);
                if (collection.Count <= 1) return null;

                collection.Coalesce();
                foreach (var frame in collection)
                {
                    token.ThrowIfCancellationRequested();
                    if (options.ResizeEnabled)
                        frame.Resize(new MagickGeometry((uint)options.Width, (uint)options.Height) { IgnoreAspectRatio = !options.KeepAspectRatio });
                    frame.Quality = (uint)options.Quality;
                    if (options.OptimizeSize) frame.Strip();
                }
                if (format == MagickFormat.Gif) collection.Optimize();

                using var ms = new MemoryStream();
                collection.Write(ms, format);
                return ms.ToArray();
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }   // не получилось — обычная одно-кадровая конвертация
        }

        private static bool IsIcon(MagickFormat format) => format == MagickFormat.Icon || format == MagickFormat.Ico;

        private static void ValidateOptions(ImageConversionOptions options)
        {
            if (!options.ResizeEnabled) return;

            if (options.Width < 1 || options.Width > 8192 || options.Height < 1 || options.Height > 8192)
                throw new ArgumentException("Недопустимый размер изображения. Допустимый диапазон: 1-8192 пикселей");

            if ((long)options.Width * options.Height > 67_108_864)
                throw new ArgumentException("Слишком большое изображение. Максимум: 8192x8192 пикселей");
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
                // Популярные
                "jpg" or "jpeg" => MagickFormat.Jpeg,
                "png" => MagickFormat.Png,
                "webp" => MagickFormat.WebP,
                "avif" => MagickFormat.Avif,
                "gif" => MagickFormat.Gif,
                "heic" or "heif" => MagickFormat.Heic,

                // Дизайн/печать
                "tiff" or "tif" => MagickFormat.Tiff,
                "bmp" => MagickFormat.Bmp,
                "pnm" or "ppm" => MagickFormat.Ppm,
                "pbm" => MagickFormat.Pbm,
                "pgm" => MagickFormat.Pgm,
                "psd" => MagickFormat.Psd,

                // Игры
                "tga" => MagickFormat.Tga,
                "dds" => MagickFormat.Dds,
                "pcx" => MagickFormat.Pcx,
                "sgi" or "rgb" => MagickFormat.Sgi,

                // VFX/видео
                "dpx" => MagickFormat.Dpx,
                "exr" => MagickFormat.Exr,
                "hdr" => MagickFormat.Hdr,
                "cin" => MagickFormat.Cin,

                // Спецформаты
                "ico" => MagickFormat.Ico,
                "jp2" => MagickFormat.Jp2,
                "jxl" => MagickFormat.Jxl,
                "qoi" => MagickFormat.Qoi,
                "ff" or "farbfeld" => MagickFormat.Farbfeld,
                _ => MagickFormat.Png
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