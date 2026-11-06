using ConverToDazz.Core.Models;
using ConverToDazz.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DazzConv.Services
{
    public record VideoPresetItem(string Key, string Name, string Badge, string Desc, string Icon, string IconClass);
    public record VideoPresetGroup(string Key, string Icon, string Title, string Hint, VideoPresetItem[] Items);

    /// <summary>
    /// Все настройки конвертации видео в ОДНОМ месте. Эту модель используют и страница «Видео», и «Пакетная конвертация»
    /// (через общую форму VideoSettingsForm), поэтому новая настройка, добавленная здесь и в форме, сразу работает в обоих местах.
    /// </summary>
    public class VideoSettings
    {
        public string OutputFormat { get; private set; } = "mp4";
        public string VideoCodec { get; set; } = "libx264";
        public string AudioCodec { get; set; } = "aac";
        public string Preset { get; set; } = "medium";
        public string Scale { get; set; } = "";
        public int Crf { get; set; } = 23;
        public int QScale { get; set; } = 2;
        public int? VideoBitrate { get; set; }
        public int? AudioBitrate { get; set; } = 192;
        public bool UseCustomFps { get; set; }
        public int CustomFps { get; set; } = 30;

        // «шакал»
        public bool AudioMono { get; set; }
        public int AudioSampleRate { get; set; }
        public int PixelateFactor { get; set; }

        public string ActivePresetName { get; set; } = "";

        public bool IsGif => OutputFormat == "gif";

        // снимок настроек перед переходом в GIF-режим
        private (string V, string A, int? Vb, int? Ab, int Crf, int Q, string P, bool Fps, int FpsV, string Scale)? _beforeGif;

        // ───────────────────────── смена формата / кодека ─────────────────────────

        public void SetOutputFormat(string newFormat)
        {
            newFormat = string.IsNullOrWhiteSpace(newFormat) ? "mp4" : newFormat.ToLowerInvariant();

            if (newFormat == "gif" && !IsGif)
            {
                _beforeGif = (VideoCodec, AudioCodec, VideoBitrate, AudioBitrate, Crf, QScale, Preset, UseCustomFps, CustomFps, Scale);
                VideoCodec = "gif"; AudioCodec = "aac";
                Crf = 20; Preset = "ultrafast";
                UseCustomFps = true; CustomFps = 15; Scale = "640:-1";
                OutputFormat = "gif";
                return;
            }

            if (newFormat != "gif" && IsGif && _beforeGif is { } s)
            {
                VideoCodec = s.V; AudioCodec = s.A; VideoBitrate = s.Vb; AudioBitrate = s.Ab;
                Crf = s.Crf; QScale = s.Q; Preset = s.P; UseCustomFps = s.Fps; CustomFps = s.FpsV; Scale = s.Scale;
                _beforeGif = null;
            }
            else if (newFormat != "gif" && IsGif)
            {
                VideoCodec = "libx264"; AudioCodec = "aac";
            }

            OutputFormat = newFormat;
            EnsureCodecsFit();
        }

        /// <summary>Если выбранный кодек не подходит новому контейнеру — подставляем кодек по умолчанию для него.</summary>
        private void EnsureCodecsFit()
        {
            var c = FormatCatalog.GetVideoContainer(OutputFormat);
            if (c == null || IsGif) return;

            if (!c.VideoCodecs.Contains(VideoCodec, StringComparer.OrdinalIgnoreCase))
            {
                SetVideoCodec(c.DefaultVideo);
            }
            if (c.AudioCodecs.Length > 0 && !c.AudioCodecs.Contains(AudioCodec, StringComparer.OrdinalIgnoreCase))
                AudioCodec = c.DefaultAudio;
        }

        public void SetVideoCodec(string codec)
        {
            VideoCodec = codec;
            var info = FormatCatalog.GetVideoCodec(codec);
            if (info == null) return;

            if (info.IsGpu) Preset = "medium";
            // у каждого кодека своя шкала CRF: подтягиваем значение в допустимый диапазон и ставим разумное по умолчанию
            if (info.Quality is QualityKind.Crf or QualityKind.Nvenc)
            {
                if (Crf > info.QualityMax) Crf = info.QualityMax;
                if (codec is "libvpx-vp9" or "libsvtav1" && Crf < 20) Crf = 32;
            }
        }

        public bool UsesCrf => FormatCatalog.GetVideoCodec(VideoCodec)?.Quality is QualityKind.Crf or QualityKind.Nvenc && !IsGif;
        public bool UsesQScale => FormatCatalog.GetVideoCodec(VideoCodec)?.Quality == QualityKind.QScale;
        public int CrfMax => FormatCatalog.GetVideoCodec(VideoCodec)?.QualityMax is > 0 and var m ? m : 51;
        public bool HasPresets => FormatCatalog.GetVideoCodec(VideoCodec)?.HasPresets ?? false;
        public bool HasAudio => FormatCatalog.GetVideoContainer(OutputFormat)?.AudioCodecs.Length != 0 && !IsGif;

        // ───────────────────────── проверка и преобразование ─────────────────────────

        public bool IsValidFps => CustomFps is >= 1 and <= 240;
        public bool IsValidVideoBitrate => !VideoBitrate.HasValue || VideoBitrate.Value > 0;
        public bool IsValidAudioBitrate => !AudioBitrate.HasValue || AudioBitrate.Value is >= 8 and <= 640;

        /// <summary>null — всё в порядке, иначе текст ошибки для пользователя.</summary>
        public string? Validate()
        {
            if (UsesCrf && (Crf < 0 || Crf > CrfMax)) return $"❌ CRF должен быть от 0 до {CrfMax}";
            if (!IsValidVideoBitrate) return "❌ Видео битрейт должен быть положительным";
            if (HasAudio && !IsValidAudioBitrate) return "❌ Аудио битрейт должен быть от 8 до 640";
            if (UseCustomFps && !IsValidFps) return "❌ FPS должен быть от 1 до 240";
            return null;
        }

        public VideoConversionOptions ToOptions() => new()
        {
            VideoCodec = VideoCodec,
            AudioCodec = AudioCodec,
            VideoBitrate = VideoBitrate,
            AudioBitrate = AudioBitrate,
            Crf = Crf,
            QScale = QScale,
            Preset = Preset,
            Scale = Scale,
            Fps = UseCustomFps ? CustomFps.ToString() : ""
        };

        public VideoExtraOptions ToExtra() => new()
        {
            AudioChannels = AudioMono ? 1 : 0,
            AudioSampleRate = AudioSampleRate,
            PixelateFactor = PixelateFactor
        };

        public VideoSettings Clone()
        {
            var c = (VideoSettings)MemberwiseClone();
            return c;
        }

        // ───────────────────────── описания для интерфейса ─────────────────────────

        public static string CrfDescription(int crf) =>
            crf <= 10 ? "🌟 Без потерь" : crf <= 18 ? "⭐ Отличное" : crf <= 23 ? "✅ Стандарт" :
            crf <= 28 ? "👍 Хорошее" : crf <= 35 ? "📱 Среднее" : "📉 Низкое";

        public static string PresetDescription(string preset) => preset switch
        {
            "ultrafast" => "⚡ Очень быстро", "superfast" => "⚡ Быстро", "veryfast" => "⚡ Быстро",
            "faster" => "🏃 Быстрее", "fast" => "🏃 Быстро", "medium" => "⚖️ Баланс",
            "slow" => "🐢 Медленно", "slower" => "🐢 Медленнее", "veryslow" => "🐌 Очень медленно", _ => preset
        };

        // ───────────────────────── быстрые пресеты ─────────────────────────

        public static readonly VideoPresetGroup[] PresetGroups =
        {
            new("universal", "🌐", "Универсальные", "Обычные задачи: интернет, телефон, максимальное качество", new VideoPresetItem[]
            {
                new("web", "Для интернета", "H.264", "MP4, H.264, AAC • Совместимо со всеми устройствами", "bi-globe", "web"),
                new("quality", "Максимальное качество", "H.265", "MKV, H.265 CRF 18 • Для архива и просмотра", "bi-stars", "quality"),
                new("fast", "Быстрая конвертация", "", "MP4, H.264 Ultrafast • Когда нужно срочно", "bi-lightning", "fast"),
                new("mobile", "Для телефона", "", "MP4 720p, H.264 • Компактный размер", "bi-phone", "mobile"),
                new("nvenc", "NVIDIA NVENC", "GPU", "Аппаратное ускорение • В 5-10 раз быстрее", "bi-gpu-card", "nvenc"),
            }),
            new("shakal", "📉", "Сильное сжатие («шакал»)", "Намеренно плохое качество и маленький размер", new VideoPresetItem[]
            {
                new("shakal1", "Шакал: лёгкий", "480p", "MP4, CRF 32, 24 fps, звук 96 кбит/с", "bi-emoji-dizzy", "meme"),
                new("shakal2", "Шакал: средний", "360p", "MP4, CRF 38, 20 fps, звук 48 кбит/с моно", "bi-emoji-dizzy", "meme"),
                new("shakal3", "Шакал: сильный", "240p", "MP4, CRF 44, 15 fps, звук 32 кбит/с моно", "bi-emoji-dizzy", "meme"),
                new("shakal4", "Шакал: ультра", "144p", "MP4, CRF 51, 10 fps, звук 16 кбит/с, 8 кГц", "bi-emoji-dizzy", "meme"),
                new("shakal_pixel", "Шакал: пиксели", "×6", "Исходный размер кадра, крупные квадраты", "bi-emoji-dizzy", "meme"),
                new("meme", "Мемный", "320×240", "Минимальный размер, 15 FPS • Для Discord/мемов", "bi-emoji-laughing", "meme"),
            }),
            new("retro", "🕰️", "Старые форматы", "3GP, AVI, WebM, WMV, FLV — как на старых устройствах", new VideoPresetItem[]
            {
                new("shakal_3gp", "Старый телефон", "3GP", "176×144, MPEG-4, 12 fps, звук 24 кбит/с", "bi-emoji-dizzy", "meme"),
                new("shakal_avi", "Ретро AVI", "AVI", "320×240, MPEG-4, звук MP3 32 кбит/с моно", "bi-emoji-dizzy", "meme"),
                new("shakal_wmv", "Старый WMV", "WMV", "320×240, WMV 8, звук WMA", "bi-emoji-dizzy", "meme"),
                new("shakal_flv", "Старый FLV", "FLV", "320×240, FLV, звук MP3 моно", "bi-emoji-dizzy", "meme"),
                new("shakal_webm", "Шакал WebM", "VP9", "320×240, VP9 CRF 51, Vorbis моно", "bi-emoji-dizzy", "meme"),
            }),
        };

        public static VideoPresetItem? FindPreset(string key) =>
            PresetGroups.SelectMany(g => g.Items).FirstOrDefault(i => i.Key == key);

        /// <summary>Заполняет настройки по пресету (сбрасывая «шакальные» поля, чтобы они не «залипали»).</summary>
        public void ApplyPreset(string key)
        {
            AudioMono = false; AudioSampleRate = 0; PixelateFactor = 0;
            if (IsGif) SetOutputFormat("mp4");

            void Set(string fmt, string v, string a, int crf, string preset, string scale, int? fps, int? vb, int ab, int qscale = 2)
            {
                SetOutputFormat(fmt);
                SetVideoCodec(v); AudioCodec = a; Crf = crf; Preset = preset; Scale = scale; QScale = qscale;
                UseCustomFps = fps.HasValue; if (fps.HasValue) CustomFps = fps.Value;
                VideoBitrate = vb; AudioBitrate = ab;
            }

            switch (key)
            {
                case "web":     Set("mp4", "libx264", "aac", 23, "medium", "", null, null, 192); break;
                case "quality": Set("mkv", "libx265", "aac", 18, "veryslow", "", null, null, 320); break;
                case "fast":    Set("mp4", "libx264", "aac", 28, "ultrafast", "", null, null, 128); break;
                case "mobile":  Set("mp4", "libx264", "aac", 26, "fast", "1280x720", 30, null, 128); break;
                case "nvenc":   Set("mp4", "h264_nvenc", "aac", 23, "medium", "", null, null, 192); break;
                case "meme":    Set("mp4", "libx264", "libmp3lame", 40, "ultrafast", "320x240", 15, 500, 64); break;

                case "shakal1": Set("mp4", "libx264", "aac", 32, "veryfast", "854x480", 24, null, 96); break;
                case "shakal2": Set("mp4", "libx264", "aac", 38, "veryfast", "640x360", 20, null, 48); AudioMono = true; AudioSampleRate = 22050; break;
                case "shakal3": Set("mp4", "libx264", "aac", 44, "ultrafast", "426x240", 15, null, 32); AudioMono = true; AudioSampleRate = 16000; break;
                case "shakal4": Set("mp4", "libx264", "aac", 51, "ultrafast", "256x144", 10, 60, 16); AudioMono = true; AudioSampleRate = 8000; break;
                case "shakal_pixel":
                    Set("mp4", "libx264", "aac", 45, "veryfast", "", 15, null, 24);
                    AudioMono = true; AudioSampleRate = 11025; PixelateFactor = 6; break;

                case "shakal_3gp":
                    Set("3gp", "mpeg4", "aac", 23, "medium", "176x144", 12, null, 24, qscale: 31);
                    AudioMono = true; AudioSampleRate = 16000; break;
                case "shakal_avi":
                    Set("avi", "mpeg4", "libmp3lame", 23, "medium", "320x240", 15, null, 32, qscale: 31);
                    AudioMono = true; AudioSampleRate = 22050; break;
                case "shakal_wmv":
                    Set("wmv", "wmv2", "wmav2", 23, "medium", "320x240", 15, null, 32, qscale: 31);
                    AudioMono = true; AudioSampleRate = 22050; break;
                case "shakal_flv":
                    Set("flv", "flv", "libmp3lame", 23, "medium", "320x240", 15, null, 32, qscale: 31);
                    AudioMono = true; AudioSampleRate = 22050; break;
                case "shakal_webm":
                    Set("webm", "libvpx-vp9", "libvorbis", 51, "medium", "320x240", 15, null, 32);
                    AudioMono = true; AudioSampleRate = 16000; break;
            }

            ActivePresetName = FindPreset(key)?.Name ?? "";
        }
    }
}
