using System.Collections.Generic;

namespace PoiTime.WinUI.Models
{
    public class VoiceItem
    {
        public int hour { get; set; }
        public int minute { get; set; }
        public string? fileName { get; set; }
    }

    public class SpecialVoice
    {
        public string? date { get; set; }
        public string? fileName { get; set; }
    }

    public class VoiceConfig
    {
        public int voiceCount { get; set; }
        public string? name { get; set; }
        public List<VoiceItem>? voices { get; set; }
        public SpecialVoice? special { get; set; }
    }
}