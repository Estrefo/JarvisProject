using JarvisProject.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace JarvisProject
{
    public partial class MainWindow : Window
    {
        private enum Estado
        {
            Libre,
            EsperandoJarvis,
            PreparandoMicrofono,
            Escuchando,
            Transcribiendo,
            Pensando,
            Hablando,
            Cerrando
        }

        private Estado _estado = Estado.Libre;
        private bool _conversacionActiva;
        private bool _iniciado;
        private bool _cerrando;

        private readonly SpeechService _speech = new();
        private readonly WindowsActionsService _windowsActions = new();
        private SpeechToTextService? _speechToText;
        private WakeWordService? _wake;

        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromMinutes(3)
        };

        private readonly List<object> _history = new()
        {
            new
            {
                role = "system",
                content = """
                    Eres Jarvis, un asistente personal de escritorio.
                    Responde en español de España, con naturalidad y brevedad.
                    La conversación se escuchará en voz alta: evita Markdown y emojis.
                    El reconocimiento de voz puede cometer errores de transcripción.
                    Si no entiendes algo, pide una aclaración sin inventarte datos.
                    La aplicación puede ejecutar una lista LIMITADA de acciones locales:
                    abrir YouTube Music, navegador, Explorador, Bloc de notas,
                    Visual Studio, controlar sesiones multimedia de Chrome y volumen.
                    Esas acciones las ejecuta el programa, no tú directamente.
                    No puedes controlar cualquier aplicación, pulsar botones
                    concretos, leer archivos, ver la pantalla ni ejecutar comandos.
                    Nunca afirmes haber hecho acciones que no se han ejecutado.
                    """
            }
        };

        public MainWindow()
        {
            InitializeComponent();

            // Se conserva la ubicación que ya utilizabas en tu ordenador.
            string modelsDirectory =
                @"C:\Users\Usuario\source\repos\JarvisProject\Models";

            try
            {
                string whisperPath = Path.Combine(modelsDirectory, "ggml-small.bin");
                _speechToText = new SpeechToTextService(whisperPath);

                _speechToText.AutoStopRequested += motivo =>
                {
                    if (_cerrando || Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        await ProcesarFinAutomaticoAsync(motivo);
                    }));
                };

                Escribir("Whisper iniciado correctamente.");
            }
            catch (Exception ex)
            {
                Escribir($"Error iniciando Whisper: {ex.Message}");
                MicButton.IsEnabled = false;
            }

            try
            {
                string voskPath = Path.Combine(
                    modelsDirectory, "vosk-model-small-es-0.42");
                _wake = new WakeWordService(voskPath);

                _wake.WakeDetected += (_, _) =>
                {
                    if (_cerrando || Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(new Action(AlDetectarJarvis));
                };

                _wake.PhraseRecognized += frase =>
                {
                    if (_cerrando || Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                        Escribir($"Vosk escuchó: [{frase}]")));
                };

                _wake.ErrorOccurred += ex =>
                {
                    if (_cerrando || Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                        Escribir($"Error de Vosk: {ex.Message}")));
                };

                Escribir("Detector de activación preparado.");
            }
            catch (Exception ex)
            {
                WakeEnabledCheckBox.IsEnabled = false;
                Escribir($"Error iniciando detector: {ex.Message}");
            }

            _speech.Finished += (_, _) =>
            {
                if (_cerrando || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    if (_estado == Estado.Hablando)
                        await ContinuarOReposarAsync();
                }));
            };

            Escribir($"Voz instalada: {_speech.VoiceName}");

            Loaded += (_, _) =>
            {
                _iniciado = true;
                ActivarDetectorSiCorresponde();
            };
        }

        private void Escribir(string texto)
        {
            if (_cerrando) return;
            ChatLog.AppendText(texto + Environment.NewLine);
            ChatLog.ScrollToEnd();
        }

        private void ActualizarBotones()
        {
            if (_cerrando) return;

            bool ocupado = _estado is Estado.PreparandoMicrofono
                or Estado.Transcribiendo or Estado.Pensando;

            SendButton.IsEnabled = !ocupado;
            PromptBox.IsEnabled = !ocupado;
            MicButton.IsEnabled = _speechToText != null && !ocupado;
            MicButton.Content = _estado == Estado.Escuchando
                ? "Detener grabación" : "🎤 Hablar";
        }

        private void ActivarDetectorSiCorresponde()
        {
            if (!_iniciado || _cerrando ||
                _estado != Estado.Libre ||
                _conversacionActiva ||
                WakeEnabledCheckBox.IsChecked != true ||
                _wake == null || _speechToText == null)
                return;

            try
            {
                if (_wake.IsBusy) return;
                _wake.Start();
                _estado = Estado.EsperandoJarvis;
                Escribir("Detector de activación escuchando.");
            }
            catch (Exception ex)
            {
                Escribir($"No se pudo activar Vosk: {ex.Message}");
            }
        }

        private void AlDetectarJarvis()
        {
            // WakeDetected llega DESPUÉS de liberar el micrófono de Vosk.
            if (_cerrando || _estado != Estado.EsperandoJarvis ||
                WakeEnabledCheckBox.IsChecked != true)
                return;

            _estado = Estado.Libre;
            _conversacionActiva = true;
            IniciarGrabacion("Jarvis activado. Te escucho...");
        }

        private void IniciarGrabacion(string mensaje)
        {
            if (_cerrando || _speechToText == null ||
                _estado != Estado.Libre)
                return;

            try
            {
                _speech.Stop();
                _speechToText.Start();
                _estado = Estado.Escuchando;
                Escribir(mensaje);
                ActualizarBotones();
            }
            catch (Exception ex)
            {
                Escribir($"Error al abrir el micrófono: {ex.Message}");
                _conversacionActiva = false;
                _estado = Estado.Libre;
                ActualizarBotones();
                ActivarDetectorSiCorresponde();
            }
        }

        private async Task ProcesarFinAutomaticoAsync(FinGrabacion motivo)
        {
            if (_cerrando || _estado != Estado.Escuchando) return;

            if (motivo == FinGrabacion.SinVoz)
            {
                _estado = Estado.PreparandoMicrofono;
                ActualizarBotones();
                try
                {
                    if (_speechToText != null)
                        await _speechToText.CancelAsync();

                    Escribir("Conversación terminada por inactividad.");
                }
                catch (Exception ex)
                {
                    Escribir($"Error cerrando micrófono: {ex.Message}");
                }
                finally
                {
                    if (!_cerrando)
                    {
                        _conversacionActiva = false;
                        _estado = Estado.Libre;
                        ActualizarBotones();
                        ActivarDetectorSiCorresponde();
                    }
                }
                return;
            }

            await TerminarYTranscribirAsync();
        }

        private async Task TerminarYTranscribirAsync()
        {
            if (_cerrando || _estado != Estado.Escuchando ||
                _speechToText == null)
                return;

            _estado = Estado.Transcribiendo;
            ActualizarBotones();
            Escribir("Procesando audio con Whisper...");

            try
            {
                var reloj = Stopwatch.StartNew();
                string texto = await _speechToText.StopAsync();
                reloj.Stop();
                if (_cerrando) return;

                Escribir($"Whisper tardó {reloj.Elapsed.TotalSeconds:F1} s.");

                if (string.IsNullOrWhiteSpace(texto))
                {
                    Escribir("No he entendido lo que has dicho.");
                    await ContinuarOReposarAsync();
                    return;
                }

                Escribir($"Reconocido: {texto}");
                await PreguntarAsync(texto);
            }
            catch (Exception ex)
            {
                Escribir($"Error transcribiendo: {ex.Message}");
                await ContinuarOReposarAsync();
            }
        }

        private async Task PreguntarAsync(string texto)
        {
            if (_cerrando) return;

            _estado = Estado.Pensando;
            ActualizarBotones();
            Escribir($"Tú: {texto}");

            // Primero verificamos si es una orden local PREDEFINIDA.
            // Si no lo es, seguimos con la conversación normal con Ollama.
            if (_windowsActions.TryRecognize(texto, out var localAction))
            {
                try
                {
                    string resultado = await _windowsActions.ExecuteAsync(localAction);
                    _history.Add(new { role = "user", content = texto });
                    _history.Add(new { role = "assistant", content = resultado });
                    Escribir($"Jarvis (Windows): {resultado}{Environment.NewLine}");

                    if (VoiceEnabledCheckBox.IsChecked == true)
                    {
                        _estado = Estado.Hablando;
                        _speech.Speak(resultado);
                    }
                    else
                    {
                        await ContinuarOReposarAsync();
                    }
                }
                catch (Exception ex)
                {
                    Escribir($"Error al ejecutar orden local: {ex.Message}");
                    await ContinuarOReposarAsync();
                }
                finally
                {
                    ActualizarBotones();
                }
                return;
            }

            _history.Add(new { role = "user", content = texto });
            bool respuestaRecibida = false;

            try
            {
                var request = new
                {
                    model = "qwen3.5:4b-q4_K_M",
                    messages = _history,
                    stream = false,
                    think = false
                };

                using var response = await Client.PostAsJsonAsync(
                    "http://127.0.0.1:11434/api/chat", request);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var json = await JsonDocument.ParseAsync(stream);
                if (_cerrando) return;

                string respuesta = json.RootElement
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";

                if (string.IsNullOrWhiteSpace(respuesta))
                    throw new InvalidOperationException("Ollama devolvió texto vacío.");

                _history.Add(new { role = "assistant", content = respuesta });
                respuestaRecibida = true;
                Escribir($"Jarvis: {respuesta}{Environment.NewLine}");

                if (VoiceEnabledCheckBox.IsChecked == true)
                {
                    _estado = Estado.Hablando;
                    _speech.Speak(respuesta);
                }
                else
                {
                    await ContinuarOReposarAsync();
                }
            }
            catch (Exception ex)
            {
                if (!respuestaRecibida && _history.Count > 1)
                    _history.RemoveAt(_history.Count - 1);

                Escribir($"Error de conversación: {ex.Message}");
                await ContinuarOReposarAsync();
            }
            finally
            {
                ActualizarBotones();
            }
        }

        private async Task ContinuarOReposarAsync()
        {
            if (_cerrando) return;

            _estado = Estado.Libre;
            ActualizarBotones();

            // Evita que el micrófono capte el final de la voz de Jarvis.
            await Task.Delay(350);
            if (_cerrando || _estado != Estado.Libre) return;

            if (_conversacionActiva &&
                WakeEnabledCheckBox.IsChecked == true &&
                _speechToText != null)
            {
                IniciarGrabacion("Puedes seguir hablando, te escucho...");
            }
            else
            {
                _conversacionActiva = false;
                ActivarDetectorSiCorresponde();
            }
        }

        private async void MicButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cerrando || _speechToText == null) return;

            if (_estado == Estado.Escuchando)
            {
                await TerminarYTranscribirAsync();
                return;
            }

            if (_estado != Estado.Libre &&
                _estado != Estado.EsperandoJarvis &&
                _estado != Estado.Hablando)
                return;

            _estado = Estado.PreparandoMicrofono;
            ActualizarBotones();

            try
            {
                if (_wake != null)
                    await _wake.StopAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5));

                if (_cerrando) return;
                _conversacionActiva = WakeEnabledCheckBox.IsChecked == true;
                _estado = Estado.Libre;
                IniciarGrabacion("Escuchando con Whisper...");
            }
            catch (Exception ex)
            {
                Escribir($"Error al preparar micrófono: {ex.Message}");
                _conversacionActiva = false;
                _estado = Estado.Libre;
                ActualizarBotones();
                ActivarDetectorSiCorresponde();
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await EnviarTextoAsync();
        }

        private async void PromptBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await EnviarTextoAsync();
            }
        }

        private async Task EnviarTextoAsync()
        {
            if (_cerrando || _estado is Estado.PreparandoMicrofono
                or Estado.Transcribiendo or Estado.Pensando)
                return;

            string texto = PromptBox.Text.Trim();
            if (texto.Length == 0) return;
            PromptBox.Clear();

            _estado = Estado.PreparandoMicrofono;
            ActualizarBotones();

            try
            {
                // Si se estaba grabando, descartamos esa captura.
                if (_speechToText?.IsRecording == true)
                    await _speechToText.CancelAsync();

                if (_wake != null)
                    await _wake.StopAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5));

                if (_cerrando) return;
                _speech.Stop();
                _conversacionActiva = WakeEnabledCheckBox.IsChecked == true;
                await PreguntarAsync(texto);
            }
            catch (Exception ex)
            {
                Escribir($"Error enviando texto: {ex.Message}");
                _estado = Estado.Libre;
                _conversacionActiva = false;
                ActualizarBotones();
                ActivarDetectorSiCorresponde();
            }
        }

        private void StopVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            _speech.Stop();
            if (_estado == Estado.Hablando)
                _ = ContinuarOReposarAsync();
        }

        private async void WakeEnabledChanged(object sender, RoutedEventArgs e)
        {
            if (!_iniciado || _cerrando) return;

            if (WakeEnabledCheckBox.IsChecked == true)
            {
                ActivarDetectorSiCorresponde();
                return;
            }

            _conversacionActiva = false;

            if (_estado == Estado.EsperandoJarvis ||
                _estado == Estado.Escuchando)
            {
                _estado = Estado.PreparandoMicrofono;
                ActualizarBotones();

                try
                {
                    if (_speechToText?.IsRecording == true)
                        await _speechToText.CancelAsync();

                    if (_wake != null)
                        await _wake.StopAsync()
                            .WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    Escribir($"Error desactivando escucha: {ex.Message}");
                }
                finally
                {
                    if (!_cerrando)
                    {
                        _estado = Estado.Libre;
                        ActualizarBotones();
                    }
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _cerrando = true;
            _estado = Estado.Cerrando;
            _wake?.Dispose();
            _speechToText?.Dispose();
            _speech.Dispose();
            base.OnClosed(e);
        }
    }
}
