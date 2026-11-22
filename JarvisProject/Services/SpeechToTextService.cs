using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Whisper.net;

namespace JarvisProject.Services
{
    public enum FinGrabacion
    {
        SilencioTrasHablar,
        SinVoz,
        TiempoMaximo
    }

    public sealed class SpeechToTextService : IDisposable
    {
        private readonly WhisperFactory _factory;
        private WaveInEvent? _microphone;
        private MemoryStream? _audio;
        private TaskCompletionSource<byte[]>? _recordingStopped;
        private bool _disposed;
        private bool _autoStopSignaled;
        private bool _heardVoice;
        private DateTime _startedAt;
        private DateTime _lastVoiceAt;

        // Ajustables más adelante según el nivel de ruido de tu habitación.
        private const double VolumeThreshold = 0.015;
        private static readonly TimeSpan EndSilence = TimeSpan.FromSeconds(1.8);
        private static readonly TimeSpan NoSpeechTimeout = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan MaxRecording = TimeSpan.FromSeconds(40);

        public bool IsRecording { get; private set; }

        // Se dispara una sola vez por grabación.
        // El consumidor debe pedir StopAsync o CancelAsync desde el hilo WPF.
        public event Action<FinGrabacion>? AutoStopRequested;

        public SpeechToTextService(string modelPath)
        {
            if (!File.Exists(modelPath))
                throw new FileNotFoundException(
                    "No se encuentra el modelo Whisper.", modelPath);

            _factory = WhisperFactory.FromPath(modelPath);
        }

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SpeechToTextService));

            if (_microphone != null)
                throw new InvalidOperationException(
                    "La grabación anterior todavía no se ha liberado.");

            _audio = new MemoryStream();
            _recordingStopped = new TaskCompletionSource<byte[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _autoStopSignaled = false;
            _heardVoice = false;
            _startedAt = DateTime.UtcNow;
            _lastVoiceAt = _startedAt;

            _microphone = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };

            _microphone.DataAvailable += OnDataAvailable;
            _microphone.RecordingStopped += OnRecordingStopped;

            try
            {
                IsRecording = true;
                _microphone.StartRecording();
            }
            catch
            {
                CleanupCapture();
                throw;
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!IsRecording || _audio == null)
                return;

            // Guardamos todos los fragmentos para que Whisper reciba la frase.
            _audio.Write(e.Buffer, 0, e.BytesRecorded);
            if (_autoStopSignaled) return;

            // RMS para audio PCM de 16 bits, mono, 16 kHz.
            int count = e.BytesRecorded / 2;
            if (count == 0) return;

            double power = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                double value = sample / 32768.0;
                power += value * value;
            }

            double rms = Math.Sqrt(power / count);
            DateTime now = DateTime.UtcNow;

            if (rms > VolumeThreshold)
            {
                _heardVoice = true;
                _lastVoiceAt = now;
            }

            FinGrabacion? reason = null;

            if (!_heardVoice && now - _startedAt >= NoSpeechTimeout)
                reason = FinGrabacion.SinVoz;
            else if (_heardVoice && now - _lastVoiceAt >= EndSilence)
                reason = FinGrabacion.SilencioTrasHablar;
            else if (now - _startedAt >= MaxRecording)
                reason = FinGrabacion.TiempoMaximo;

            if (reason != null)
            {
                // NO llamamos StopRecording dentro de DataAvailable.
                _autoStopSignaled = true;
                AutoStopRequested?.Invoke(reason.Value);
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                _recordingStopped?.TrySetException(e.Exception);
            }
            else
            {
                _recordingStopped?.TrySetResult(
                    _audio?.ToArray() ?? Array.Empty<byte>());
            }
        }

        private async Task<byte[]> StopCaptureAsync()
        {
            if (!IsRecording || _microphone == null ||
                _recordingStopped == null)
                return Array.Empty<byte>();

            IsRecording = false;

            try
            {
                _microphone.StopRecording();
                // Esperamos a RecordingStopped antes de eliminar los recursos.
                return await _recordingStopped.Task
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                CleanupCapture();
            }
        }

        public async Task<string> StopAsync()
        {
            byte[] pcm = await StopCaptureAsync();
            if (pcm.Length == 0) return string.Empty;

            // El trabajo pesado de Whisper NO se ejecuta en el hilo de WPF.
            return await Task.Run(() => TranscribeAsync(pcm));
        }

        public async Task CancelAsync()
        {
            // Liberar el micrófono sin enviar nada a Whisper.
            await StopCaptureAsync();
        }

        private async Task<string> TranscribeAsync(byte[] pcm)
        {
            byte[] wav;

            using (var buffer = new MemoryStream())
            {
                using (var writer = new WaveFileWriter(
                    buffer, new WaveFormat(16000, 16, 1)))
                {
                    writer.Write(pcm, 0, pcm.Length);
                }

                wav = buffer.ToArray();
            }

            using var stream = new MemoryStream(wav);
            using var processor = _factory.CreateBuilder()
                .WithLanguage("es")
                .WithNoContext()
                .Build();

            var words = new List<string>();
            await foreach (var segment in processor.ProcessAsync(stream))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                    words.Add(segment.Text.Trim());
            }

            return string.Join(" ", words).Trim();
        }

        private void CleanupCapture()
        {
            IsRecording = false;

            if (_microphone != null)
            {
                _microphone.DataAvailable -= OnDataAvailable;
                _microphone.RecordingStopped -= OnRecordingStopped;
                _microphone.Dispose();
                _microphone = null;
            }

            _audio?.Dispose();
            _audio = null;
            _recordingStopped = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_microphone != null)
            {
                _microphone.DataAvailable -= OnDataAvailable;
                _microphone.RecordingStopped -= OnRecordingStopped;
                try { _microphone.StopRecording(); } catch { }
            }

            CleanupCapture();
            _factory.Dispose();
        }
    }
}
