using NAudio.Wave;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vosk;

namespace JarvisProject.Services
{
    public sealed class WakeWordService : IDisposable
    {
        private readonly Model _model;
        private WaveInEvent? _microphone;
        private VoskRecognizer? _recognizer;
        private TaskCompletionSource<bool>? _stopSource;
        private SynchronizationContext? _uiContext;

        private volatile bool _listening;
        private volatile bool _pendingWake;
        private bool _disposed;

        public bool IsListening => _listening;
        public bool IsBusy => _microphone != null;

        public event EventHandler? WakeDetected;
        public event Action<string>? PhraseRecognized;
        public event Action<Exception>? ErrorOccurred;

        public WakeWordService(string modelPath)
        {
            if (!Directory.Exists(modelPath))
                throw new DirectoryNotFoundException(modelPath);

            Vosk.Vosk.SetLogLevel(-1);
            _model = new Model(modelPath);
        }

        // Llamar desde el hilo UI de WPF.
        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WakeWordService));

            if (_microphone != null)
                return; // Ya grabando o terminando de liberar el dispositivo.

            _uiContext = SynchronizationContext.Current;

            string grammar = JsonSerializer.Serialize(new[]
            {
                "hola jarvis", "oye jarvis",
                "hola asistente", "oye asistente", "[unk]"
            });

            _recognizer = new VoskRecognizer(_model, 16000.0f, grammar);
            _microphone = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };

            _microphone.DataAvailable += OnAudio;
            _microphone.RecordingStopped += OnStopped;

            try
            {
                _pendingWake = false;
                _listening = true;
                _microphone.StartRecording();
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        private void OnAudio(object? sender, WaveInEventArgs e)
        {
            if (!_listening || _recognizer == null)
                return;

            try
            {
                if (!_recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
                    return;

                using var json = JsonDocument.Parse(_recognizer.Result());
                string text = json.RootElement.GetProperty("text").GetString() ?? "";

                // Diagnóstico: permitirá ver qué oye realmente Vosk.
                if (!string.IsNullOrWhiteSpace(text))
                    PhraseRecognized?.Invoke(text);

                if (!IsWakePhrase(text))
                    return;

                _pendingWake = true;
                _listening = false;

                // IMPORTANTE: no detener WaveInEvent dentro de DataAvailable.
                // Lo pedimos al hilo UI, cuando haya terminado este callback.
                if (_uiContext != null)
                {
                    _uiContext.Post(state => { _ = StopAfterWakeAsync(); }, null);
                }
                else
                {
                    _ = Task.Run(StopAfterWakeAsync);
                }
            }
            catch (Exception ex)
            {
                _listening = false;
                ErrorOccurred?.Invoke(ex);
                if (_uiContext != null)
                    _uiContext.Post(state => { _ = StopAsync(); }, null);
                else
                    _ = Task.Run(StopAsync);
            }
        }

        private static bool IsWakePhrase(string text)
        {
            return text.Trim().ToLowerInvariant() switch
            {
                "hola jarvis" => true,
                "oye jarvis" => true,
                "hola asistente" => true,
                "oye asistente" => true,
                _ => false
            };
        }

        private async Task StopAfterWakeAsync()
        {
            try
            {
                await StopCoreAsync(cancelWake: false);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(ex);
            }
        }

        public Task StopAsync() => StopCoreAsync(cancelWake: true);

        private Task StopCoreAsync(bool cancelWake)
        {
            if (cancelWake)
                _pendingWake = false;

            if (_microphone == null)
                return Task.CompletedTask;

            if (_stopSource != null)
                return _stopSource.Task;

            _stopSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task result = _stopSource.Task;
            _listening = false;

            try
            {
                _microphone.StopRecording();
            }
            catch (Exception ex)
            {
                Cleanup();
                _stopSource?.TrySetException(ex);
                _stopSource = null;
            }

            return result;
        }

        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            bool shouldWake = _pendingWake && !_disposed && e.Exception == null;
            _pendingWake = false;

            Cleanup();

            var completion = _stopSource;
            _stopSource = null;

            if (e.Exception != null)
            {
                completion?.TrySetException(e.Exception);
                ErrorOccurred?.Invoke(e.Exception);
            }
            else
            {
                completion?.TrySetResult(true);
                if (shouldWake)
                    WakeDetected?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Cleanup()
        {
            _listening = false;
            if (_microphone != null)
            {
                _microphone.DataAvailable -= OnAudio;
                _microphone.RecordingStopped -= OnStopped;
                _microphone.Dispose();
                _microphone = null;
            }

            _recognizer?.Dispose();
            _recognizer = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _listening = false;
            _pendingWake = false;

            if (_microphone != null)
            {
                _microphone.DataAvailable -= OnAudio;
                _microphone.RecordingStopped -= OnStopped;
                try { _microphone.StopRecording(); } catch { }
            }
            Cleanup();
            _stopSource?.TrySetResult(true);
            _stopSource = null;
            _model.Dispose();
        }
    }
}
