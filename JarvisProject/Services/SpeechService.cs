using System;
using System.Linq;
using System.Speech.Synthesis;

namespace JarvisProject.Services
{
    public sealed class SpeechService : IDisposable
    {
        private readonly SpeechSynthesizer _synth = new();
        private Prompt? _activePrompt;

        public string VoiceName { get; }
        public bool HasSpanishVoice { get; }
        public bool IsSpeaking => _activePrompt != null;
        public event EventHandler? Finished;

        public SpeechService()
        {
            _synth.SetOutputToDefaultAudioDevice();

            _synth.SpeakCompleted += (_, e) =>
            {
                if (!ReferenceEquals(e.Prompt, _activePrompt))
                    return;

                _activePrompt = null;
                if (!e.Cancelled && e.Error == null)
                    Finished?.Invoke(this, EventArgs.Empty);
            };

            var voices = _synth.GetInstalledVoices()
                .Where(v => v.Enabled)
                .ToList();

            var spanishVoice = voices.FirstOrDefault(v =>
                v.VoiceInfo.Culture.TwoLetterISOLanguageName == "es");

            if (spanishVoice != null)
            {
                _synth.SelectVoice(spanishVoice.VoiceInfo.Name);
                HasSpanishVoice = true;
            }

            VoiceName = voices.Count == 0
                ? "No hay voces instaladas"
                : spanishVoice?.VoiceInfo.Name ?? voices[0].VoiceInfo.Name;

            _synth.Rate = 0;
            _synth.Volume = 100;
        }

        public void Speak(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _synth.SpeakAsyncCancelAll();
            _activePrompt = new Prompt(text);
            _synth.SpeakAsync(_activePrompt);
        }

        public void Stop()
        {
            _activePrompt = null;
            _synth.SpeakAsyncCancelAll();
        }

        public void Dispose() => _synth.Dispose();
    }
}
