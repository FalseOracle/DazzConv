using ConverToDazz.Core.Services;
using System;
using System.Linq;

namespace DazzConv.Services
{
    /// <summary>
    /// Все настройки конвертации аудио в одном месте: их использует и страница «Аудио», и «Пакетная конвертация»
    /// (через общую форму AudioSettingsForm). Ограничения форматов берутся из FormatCatalog.
    /// </summary>
    public class AudioSettings
    {
        public string OutputFormat { get; private set; } = "mp3";
        public int Bitrate { get; set; } = 192;
        public int SampleRate { get; set; }
        public bool Normalize { get; set; }
        public bool Mono { get; set; }

        public AudioFormatInfo Format => FormatCatalog.GetAudioFormat(OutputFormat) ?? FormatCatalog.AudioFormats[0];

        /// <summary>Расширение выходного файла (у ALAC — m4a).</summary>
        public string OutputExt => Format.OutputExt;

        /// <summary>Меняет формат и подтягивает битрейт/частоту к допустимым для него значениям. Неизвестный формат игнорируется.</summary>
        public void SetFormat(string ext)
        {
            var f = FormatCatalog.GetAudioFormat(ext);
            if (f == null) return;

            OutputFormat = f.Ext;
            if (f.HasBitrate) Bitrate = FormatCatalog.NearestBitrate(f, Bitrate > 0 ? Bitrate : f.DefaultBitrate);
            if (SampleRate > 0 && !f.SampleRates.Contains(SampleRate)) SampleRate = FormatCatalog.NearestSampleRate(f, SampleRate);
            if (f.MaxChannels == 1) Mono = true;
        }

        public string? Validate() => null;   // значения всегда приводятся к допустимым

        public AudioConversionOptions ToOptions(bool stripVideo = false)
        {
            var f = Format;
            return new AudioConversionOptions
            {
                Codec = f.Codec,
                BitrateKbps = f.HasBitrate ? FormatCatalog.NearestBitrate(f, Bitrate) : 0,
                SampleRate = FormatCatalog.NearestSampleRate(f, SampleRate),
                Channels = Mono || f.MaxChannels == 1 ? 1 : 0,
                Normalize = Normalize,
                StripVideo = stripVideo,
                FormatExt = f.Ext
            };
        }

        public AudioSettings Clone() => (AudioSettings)MemberwiseClone();

        /// <summary>Режим «шакал»: низкий битрейт, моно и низкая частота.</summary>
        public void ApplyShakal(int level)
        {
            Normalize = false;
            switch (level)
            {
                case 1: SetFormat("mp3"); Bitrate = 64; SampleRate = 0; Mono = false; break;
                case 2: SetFormat("mp3"); Bitrate = 32; SampleRate = 22050; Mono = true; break;
                case 3: SetFormat("mp3"); Bitrate = 16; SampleRate = 11025; Mono = true; break;
                case 4: SetFormat("mp3"); Bitrate = 8; SampleRate = 8000; Mono = true; break;
                default: SetFormat("opus"); Bitrate = 8; SampleRate = 8000; Mono = true; break;
            }
            SetFormat(OutputFormat);   // подтянуть значения к допустимым
        }

        public string ShakalSummary =>
            $"{OutputFormat.ToUpper()} {Bitrate} kbps{(SampleRate > 0 ? $", {SampleRate} Hz" : "")}{(Mono ? ", моно" : "")}";
    }
}
