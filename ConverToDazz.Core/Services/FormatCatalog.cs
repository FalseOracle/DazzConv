using System;
using System.Collections.Generic;
using System.Linq;

namespace ConverToDazz.Core.Services
{
    // ═════════════════════════════════════════════════════════════════════════════════════
    //  ЕДИНЫЙ КАТАЛОГ ФОРМАТОВ.
    //  Все формы (одиночные страницы и пакетная конвертация) берут списки форматов, кодеков,
    //  диапазоны битрейтов и особенности контейнеров ТОЛЬКО отсюда. Добавили формат здесь —
    //  он появился везде и обрабатывается ffmpeg с правильными аргументами.
    // ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Чем регулируется качество видеокодека.</summary>
    public enum QualityKind
    {
        None,       // фиксированное качество (ProRes, GIF)
        Crf,        // CRF: меньше — лучше (x264, x265, VP9, AV1)
        QScale,     // шкала 1–31: меньше — лучше (MPEG-4, WMV, FLV, MPEG-2)
        Nvenc       // CQ для NVIDIA
    }

    public record VideoCodecInfo(string Id, string Name, string Hint, QualityKind Quality, int QualityMax,
        bool IsGpu = false, bool HasPresets = true);

    public record VideoContainerInfo(string Ext, string Name, string Description, string Group,
        string[] VideoCodecs, string[] AudioCodecs, string DefaultVideo, string DefaultAudio,
        string[]? ExtraArgs = null, string? Note = null);

    public record AudioCodecInfo(string Id, string Name, bool Lossy);

    public record AudioFormatInfo(string Ext, string Name, string Description, string Group,
        string Codec, bool Lossless, int[] Bitrates, int DefaultBitrate, int[] SampleRates, int MaxChannels,
        string? FileExt = null, string[]? ExtraArgs = null, string? Note = null)
    {
        /// <summary>Расширение выходного файла (у ALAC — .m4a).</summary>
        public string OutputExt => FileExt ?? Ext;
        public bool HasBitrate => !Lossless && Bitrates.Length > 0;
        public int MinBitrate => Bitrates.Length > 0 ? Bitrates.Min() : 0;
        public int MaxBitrate => Bitrates.Length > 0 ? Bitrates.Max() : 0;
    }

    public record ImageFormatInfo(string Ext, string Name, string Description, string Group,
        bool Lossy, bool SupportsQuality, bool Alpha, bool Animation, int MaxSize = 0, string? Note = null);

    public static class FormatCatalog
    {
        // ───────────────────────────── ВИДЕОКОДЕКИ ─────────────────────────────

        public static readonly IReadOnlyList<VideoCodecInfo> VideoCodecs = new List<VideoCodecInfo>
        {
            new("libx264",    "H.264 (libx264)",         "универсальный",          QualityKind.Crf, 51),
            new("libx265",    "H.265/HEVC (libx265)",    "лучшее сжатие",          QualityKind.Crf, 51),
            new("h264_nvenc", "H.264 NVENC (GPU NVIDIA)","быстро, на видеокарте",  QualityKind.Nvenc, 51, IsGpu: true),
            new("hevc_nvenc", "HEVC NVENC (GPU NVIDIA)", "быстро, на видеокарте",  QualityKind.Nvenc, 51, IsGpu: true),
            new("libvpx-vp9", "VP9 (libvpx-vp9)",        "для WebM, без лицензий", QualityKind.Crf, 63, HasPresets: false),
            new("libvpx",     "VP8 (libvpx)",            "старый WebM",            QualityKind.Crf, 63, HasPresets: false),
            new("libsvtav1",  "AV1 (SVT-AV1)",           "самое современное сжатие", QualityKind.Crf, 63),
            new("mpeg4",      "MPEG-4 Part 2",           "старая совместимость",   QualityKind.QScale, 31, HasPresets: false),
            new("wmv2",       "WMV 8 (wmv2)",            "для .wmv",               QualityKind.QScale, 31, HasPresets: false),
            new("flv",        "Sorenson (flv1)",         "для старых .flv",        QualityKind.QScale, 31, HasPresets: false),
            new("mpeg2video", "MPEG-2",                  "DVD и телевидение",      QualityKind.QScale, 31, HasPresets: false),
            new("libtheora",  "Theora",                  "для .ogv",               QualityKind.QScale, 31, HasPresets: false),
            new("prores_ks",  "Apple ProRes",            "для монтажа, огромный размер", QualityKind.None, 0, HasPresets: false),
            new("gif",        "GIF (анимация)",          "256 цветов, без звука",  QualityKind.None, 0, HasPresets: false),
        };

        public static VideoCodecInfo? GetVideoCodec(string id) =>
            VideoCodecs.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        // ───────────────────────────── АУДИОКОДЕКИ ДЛЯ ВИДЕО ─────────────────────────────

        public static readonly IReadOnlyList<AudioCodecInfo> VideoAudioCodecs = new List<AudioCodecInfo>
        {
            new("aac",        "AAC (рекомендуется)",         true),
            new("libmp3lame", "MP3",                         true),
            new("libopus",    "Opus (современный)",          true),
            new("libvorbis",  "Vorbis",                      true),
            new("ac3",        "AC3 (Dolby Digital)",         true),
            new("mp2",        "MP2 (DVD/ТВ)",                true),
            new("wmav2",      "WMA v2",                      true),
            new("flac",       "FLAC (без потерь)",           false),
            new("alac",       "ALAC (Apple, без потерь)",    false),
            new("pcm_s16le",  "PCM (без сжатия)",            false),
            new("copy",       "Копировать (без перекодирования)", false),
        };

        public static AudioCodecInfo? GetVideoAudioCodec(string id) =>
            VideoAudioCodecs.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        // ───────────────────────────── ВИДЕОКОНТЕЙНЕРЫ ─────────────────────────────
        // Для каждого контейнера перечислены ТОЛЬКО допустимые кодеки: например, в WebM нельзя положить H.264/AAC,
        // в WMV — H.264, в DVD (VOB) — H.264. Раньше пользователь мог выбрать такую пару, и ffmpeg падал с ошибкой.

        private static readonly string[] FastStart = { "-movflags", "+faststart" };

        public static readonly IReadOnlyList<VideoContainerInfo> VideoContainers = new List<VideoContainerInfo>
        {
            // Популярные
            new("mp4",  "MP4",  "универсальный, везде работает", "Популярные",
                new[] { "libx264", "libx265", "h264_nvenc", "hevc_nvenc", "libsvtav1", "libvpx-vp9", "mpeg4" },
                new[] { "aac", "libmp3lame", "ac3", "libopus", "flac", "copy" }, "libx264", "aac", FastStart),
            new("mkv",  "MKV",  "принимает почти всё, для архива", "Популярные",
                new[] { "libx264", "libx265", "h264_nvenc", "hevc_nvenc", "libsvtav1", "libvpx-vp9", "libvpx", "mpeg4" },
                new[] { "aac", "libmp3lame", "libopus", "libvorbis", "ac3", "flac", "pcm_s16le", "copy" }, "libx264", "aac"),
            new("webm", "WebM", "для сайтов (VP8/VP9/AV1 + Opus/Vorbis)", "Популярные",
                new[] { "libvpx-vp9", "libvpx", "libsvtav1" },
                new[] { "libopus", "libvorbis", "copy" }, "libvpx-vp9", "libopus"),
            new("mov",  "MOV",  "Apple QuickTime, монтаж", "Популярные",
                new[] { "libx264", "libx265", "h264_nvenc", "hevc_nvenc", "mpeg4", "prores_ks" },
                new[] { "aac", "alac", "pcm_s16le", "libmp3lame", "copy" }, "libx264", "aac", FastStart),
            new("avi",  "AVI",  "старый универсальный", "Популярные",
                new[] { "mpeg4", "libx264" },
                new[] { "libmp3lame", "ac3", "aac", "pcm_s16le", "copy" }, "mpeg4", "libmp3lame"),

            // Мобильные и Apple
            new("m4v",  "M4V",  "iTunes и устройства Apple", "Мобильные и Apple",
                new[] { "libx264", "mpeg4" }, new[] { "aac", "alac", "copy" }, "libx264", "aac", FastStart),
            new("3gp",  "3GP",  "старые телефоны (маленький размер)", "Мобильные и Apple",
                new[] { "mpeg4", "libx264" }, new[] { "aac", "copy" }, "mpeg4", "aac", FastStart,
                "Для старых телефонов подойдёт разрешение 176×144 или 352×288"),
            new("3g2",  "3G2",  "телефоны CDMA", "Мобильные и Apple",
                new[] { "mpeg4", "libx264" }, new[] { "aac", "copy" }, "mpeg4", "aac", FastStart),

            // Веб и стриминг
            new("flv",  "FLV",  "Flash-видео, потоковое вещание", "Веб и ТВ",
                new[] { "libx264", "flv" }, new[] { "aac", "libmp3lame" }, "libx264", "aac"),
            new("ts",   "TS",   "MPEG-TS: ТВ, потоки, HLS", "Веб и ТВ",
                new[] { "libx264", "libx265", "h264_nvenc", "hevc_nvenc", "mpeg2video" },
                new[] { "aac", "ac3", "mp2", "libmp3lame" }, "libx264", "aac"),
            new("m2ts", "M2TS", "камеры и Blu-ray", "Веб и ТВ",
                new[] { "libx264", "libx265", "mpeg2video" },
                new[] { "aac", "ac3", "mp2" }, "libx264", "ac3"),
            new("ogv",  "OGV",  "Ogg Theora, открытый формат", "Веб и ТВ",
                new[] { "libtheora" }, new[] { "libvorbis", "libopus" }, "libtheora", "libvorbis"),

            // Старые и DVD
            new("wmv",  "WMV",  "Windows Media", "Старые и DVD",
                new[] { "wmv2" }, new[] { "wmav2" }, "wmv2", "wmav2"),
            new("mpg",  "MPG",  "MPEG-2, DVD-совместимый", "Старые и DVD",
                new[] { "mpeg2video" }, new[] { "mp2", "ac3", "libmp3lame" }, "mpeg2video", "mp2"),
            new("mpeg", "MPEG", "MPEG-2 программный поток", "Старые и DVD",
                new[] { "mpeg2video" }, new[] { "mp2", "ac3", "libmp3lame" }, "mpeg2video", "mp2"),
            new("vob",  "VOB",  "диск DVD-Video", "Старые и DVD",
                new[] { "mpeg2video" }, new[] { "ac3", "mp2" }, "mpeg2video", "ac3"),

            // Анимация
            new("gif",  "GIF",  "анимация (без звука)", "Анимация",
                new[] { "gif" }, Array.Empty<string>(), "gif", ""),
        };

        public static VideoContainerInfo? GetVideoContainer(string ext) =>
            VideoContainers.FirstOrDefault(c => c.Ext.Equals(ext?.TrimStart('.'), StringComparison.OrdinalIgnoreCase));

        /// <summary>Подходит ли пара «контейнер + кодек видео».</summary>
        public static bool IsVideoCodecAllowed(string ext, string codec) =>
            GetVideoContainer(ext)?.VideoCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase) ?? true;

        public static bool IsAudioCodecAllowed(string ext, string codec) =>
            GetVideoContainer(ext)?.AudioCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase) ?? true;

        // ───────────────────────────── АУДИОФОРМАТЫ ─────────────────────────────

        private static readonly int[] Sr8To48 = { 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000 };
        private static readonly int[] SrLossless = { 8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000 };
        private static readonly int[] None = Array.Empty<int>();

        public static readonly IReadOnlyList<AudioFormatInfo> AudioFormats = new List<AudioFormatInfo>
        {
            // Популярные
            new("mp3",  "MP3",  "самый совместимый",                      "Популярные", "libmp3lame", false,
                new[] { 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }, 192, Sr8To48, 2),
            new("m4a",  "M4A (AAC)", "Apple и современные плееры",        "Популярные", "aac", false,
                new[] { 16, 24, 32, 48, 64, 96, 128, 160, 192, 256, 320 }, 192, Sr8To48, 2),
            new("aac",  "AAC",  "чистый поток AAC (ADTS)",                "Популярные", "aac", false,
                new[] { 16, 24, 32, 48, 64, 96, 128, 160, 192, 256, 320 }, 192, Sr8To48, 2, ExtraArgs: new[] { "-f", "adts" }),
            new("ogg",  "OGG Vorbis", "открытый формат",                  "Популярные", "libvorbis", false,
                new[] { 32, 48, 64, 96, 128, 160, 192, 256, 320 }, 160, new[] { 8000, 11025, 16000, 22050, 32000, 44100, 48000 }, 2),
            new("opus", "OPUS", "лучшее качество на малом битрейте",      "Популярные", "libopus", false,
                new[] { 8, 12, 16, 24, 32, 48, 64, 96, 128, 160, 192, 256, 320 }, 128, new[] { 8000, 12000, 16000, 24000, 48000 }, 2),

            // Без потерь
            new("flac", "FLAC", "без потерь, сжатый",                      "Без потерь", "flac", true, None, 0, SrLossless, 8,
                ExtraArgs: new[] { "-compression_level", "8" },
                Note: "Настоящий lossless только если исходник тоже без потерь"),
            new("wav",  "WAV",  "без сжатия",                             "Без потерь", "pcm_s16le", true, None, 0, SrLossless, 8),
            new("aiff", "AIFF", "без сжатия, Apple",                      "Без потерь", "pcm_s16be", true, None, 0, SrLossless, 8),
            new("alac", "ALAC", "без потерь, Apple (.m4a)",               "Без потерь", "alac", true, None, 0, SrLossless, 8, FileExt: "m4a"),
            new("wv",   "WavPack", "без потерь, эффективный",             "Без потерь", "wavpack", true, None, 0, SrLossless, 8),
            new("tta",  "TTA",  "без потерь",                             "Без потерь", "tta", true, None, 0, SrLossless, 8),
            new("caf",  "CAF",  "Core Audio (Apple), без сжатия",         "Без потерь", "pcm_s16le", true, None, 0, SrLossless, 8),

            // Специальные
            new("wma",  "WMA",  "Windows Media Audio",                    "Специальные", "wmav2", false,
                new[] { 32, 48, 64, 96, 128, 160, 192, 256, 320 }, 128, new[] { 8000, 11025, 16000, 22050, 32000, 44100, 48000 }, 2),
            new("ac3",  "AC3",  "Dolby Digital, видеомонтаж",             "Специальные", "ac3", false,
                new[] { 64, 96, 128, 192, 256, 320, 384, 448, 512, 576, 640 }, 192, new[] { 32000, 44100, 48000 }, 6,
                Note: "Битрейты строго из списка Dolby Digital; частота 32/44.1/48 кГц"),
            new("mp2",  "MP2",  "радио и ТВ",                             "Специальные", "mp2", false,
                new[] { 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 }, 192, new[] { 16000, 22050, 24000, 32000, 44100, 48000 }, 2),
            new("amr",  "AMR",  "голос для телефонов (моно, 8 кГц)",      "Специальные", "libopencore_amrnb", false,
                new[] { 5, 6, 7, 8, 10, 12 }, 12, new[] { 8000 }, 1,
                Note: "Только моно и 8000 Гц; нужен ffmpeg с поддержкой AMR"),
        };

        public static AudioFormatInfo? GetAudioFormat(string ext) =>
            AudioFormats.FirstOrDefault(f => f.Ext.Equals(ext?.TrimStart('.'), StringComparison.OrdinalIgnoreCase));

        /// <summary>Ближайший допустимый битрейт формата (иначе ffmpeg выдаёт ошибку: например, AC3 принимает только строго заданные значения).</summary>
        public static int NearestBitrate(AudioFormatInfo fmt, int requested)
        {
            if (!fmt.HasBitrate) return 0;
            return fmt.Bitrates.OrderBy(b => Math.Abs(b - requested)).First();
        }

        /// <summary>Ближайшая допустимая частота дискретизации формата (0 — «как в оригинале» остаётся 0).</summary>
        public static int NearestSampleRate(AudioFormatInfo fmt, int requested)
        {
            if (requested <= 0 || fmt.SampleRates.Length == 0) return 0;
            return fmt.SampleRates.OrderBy(r => Math.Abs(r - requested)).First();
        }

        /// <summary>Частоты для аудио внутри видео (по кодеку).</summary>
        public static int[] SampleRatesForVideoAudio(string codec) => codec switch
        {
            "libopus" => new[] { 8000, 12000, 16000, 24000, 48000 },
            "ac3" => new[] { 32000, 44100, 48000 },
            "mp2" => new[] { 16000, 22050, 24000, 32000, 44100, 48000 },
            "copy" => None,
            _ => Sr8To48
        };

        // ───────────────────────────── ИЗОБРАЖЕНИЯ ─────────────────────────────

        public static readonly IReadOnlyList<ImageFormatInfo> ImageFormats = new List<ImageFormatInfo>
        {
            // Популярные
            new("jpg",  "JPEG", "фото, маленький размер",              "Популярные", Lossy: true,  SupportsQuality: true,  Alpha: false, Animation: false),
            new("png",  "PNG",  "без потерь, прозрачность",            "Популярные", Lossy: false, SupportsQuality: false, Alpha: true,  Animation: false),
            new("webp", "WebP", "современный веб-формат",              "Популярные", Lossy: true,  SupportsQuality: true,  Alpha: true,  Animation: true),
            new("avif", "AVIF", "лучшее сжатие фото",                  "Популярные", Lossy: true,  SupportsQuality: true,  Alpha: true,  Animation: false),
            new("gif",  "GIF",  "256 цветов, анимация",                "Популярные", Lossy: false, SupportsQuality: false, Alpha: true,  Animation: true),
            new("heic", "HEIC", "формат iPhone",                       "Популярные", Lossy: true,  SupportsQuality: true,  Alpha: true,  Animation: false,
                Note: "Запись HEIC зависит от сборки библиотеки; при ошибке файл сохранится в WebP"),

            // Дизайн и печать
            new("tiff", "TIFF", "печать и архив, без потерь",          "Дизайн и печать", false, false, true,  false),
            new("bmp",  "BMP",  "без сжатия, без прозрачности",        "Дизайн и печать", false, false, false, false),
            new("psd",  "PSD",  "Photoshop",                           "Дизайн и печать", false, false, true,  false),
            new("jp2",  "JPEG 2000", "печать, медицина, архивы",       "Дизайн и печать", true,  true,  true,  false),
            new("jxl",  "JPEG XL", "преемник JPEG",                    "Дизайн и печать", true,  true,  true,  false),
            new("pnm",  "PNM/PPM", "простой формат для обработки",     "Дизайн и печать", false, false, false, false),
            new("pbm",  "PBM",  "чёрно-белый",                         "Дизайн и печать", false, false, false, false),
            new("pgm",  "PGM",  "оттенки серого",                      "Дизайн и печать", false, false, false, false),

            // Игры
            new("tga",  "TGA",  "текстуры",                            "Игры", false, false, true,  false),
            new("dds",  "DDS",  "текстуры DirectX",                    "Игры", false, false, true,  false),
            new("pcx",  "PCX",  "старые игры",                         "Игры", false, false, false, false),
            new("sgi",  "SGI",  "Silicon Graphics",                    "Игры", false, false, true,  false),

            // Видео и VFX
            new("dpx",  "DPX",  "кинопроизводство",                    "Видео и VFX", false, false, false, false),
            new("exr",  "OpenEXR", "HDR, VFX",                         "Видео и VFX", false, false, true,  false),
            new("hdr",  "HDR (Radiance)", "высокий динамический диапазон", "Видео и VFX", false, false, false, false),
            new("cin",  "Cineon", "плёнка",                            "Видео и VFX", false, false, false, false),

            // Спецформаты
            new("ico",  "ICO",  "иконки Windows (до 256×256)",         "Специальные", false, false, true,  false, MaxSize: 256),
            new("qoi",  "QOI",  "быстрый формат без потерь",           "Специальные", false, false, true,  false),
            new("ff",   "Farbfeld", "простой формат без сжатия",       "Специальные", false, false, true,  false),
        };

        public static ImageFormatInfo? GetImageFormat(string ext) =>
            ImageFormats.FirstOrDefault(f => f.Ext.Equals(ext?.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ВХОДНЫЕ форматы = те же, что можно получить на ВЫХОДЕ (все из FormatCatalog) + синонимы расширений одного и того же формата
    /// (jpeg = jpg, tif = tiff, mts = ts …). Единый источник для диалогов выбора файла, проверок и пакетной конвертации:
    /// раньше у каждой страницы был свой список, а диалог изображений использовал стандартный фильтр без WebP/AVIF/HEIC и др.
    /// </summary>
    public static class InputFormats
    {
        public static readonly string[] Video = FormatCatalog.VideoContainers.Select(c => c.Ext)
            .Concat(new[] { "mts", "m2t" })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        public static readonly string[] Audio = FormatCatalog.AudioFormats.SelectMany(f => new[] { f.Ext, f.OutputExt })
            .Concat(new[] { "m4b", "oga" })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        public static readonly string[] Image = FormatCatalog.ImageFormats.Select(f => f.Ext)
            .Concat(new[] { "jpeg", "jpe", "tif", "heif", "ppm", "rgb", "farbfeld" })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>Что это за файл: "audio", "image", "video" или null (формат не поддерживается).</summary>
        public static string? KindOf(string ext)
        {
            ext = ext.TrimStart('.').ToLowerInvariant();
            if (Audio.Contains(ext)) return "audio";
            if (Image.Contains(ext)) return "image";
            if (Video.Contains(ext)) return "video";
            return null;
        }
    }
}
