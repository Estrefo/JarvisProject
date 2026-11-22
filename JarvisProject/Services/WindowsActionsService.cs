using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace JarvisProject.Services
{
    // Solo ejecuta acciones PREDEFINIDAS. No interpreta rutas, código ni
    // comandos arbitrarios facilitados por el modelo de IA o por la voz.
    public sealed class WindowsActionsService
    {
        public enum LocalAction
        {
            OpenYoutubeMusic,
            OpenBrowser,
            OpenFileExplorer,
            OpenNotepad,
            OpenVisualStudio,
            ToggleMedia,
            PlayMedia,
            PauseMedia,
            NextTrack,
            PreviousTrack,
            VolumeUp,
            VolumeDown,
            ToggleMute
        }

        public bool TryRecognize(string spokenText, out LocalAction action)
        {
            action = default;
            string text = Normalize(spokenText);

            // Evita ejecutar instrucciones contenidas en preguntas explicativas,
            // frases negativas o menciones casuales de los programas.
            if (text.Length == 0 ||
                text.StartsWith("como ") || text.StartsWith("que ") ||
                text.StartsWith("por que ") || text.StartsWith("explicame ") ||
                text.StartsWith("dime como ") ||
                text.StartsWith("no ") || text.Contains(" no "))
                return false;

            // Los motores de voz a veces incluyen la invocación en la frase.
            text = Regex.Replace(text,
                @"^(?:(?:hola|oye|eh) )?jarvis(?: |$)", "").Trim();

            // Elegimos una sola acción por frase, sin combinar comandos todavía.
            if (IsOpenCommand(text))
            {
                if (Has(text, "youtube music") || Has(text, "youtube musica"))
                    action = LocalAction.OpenYoutubeMusic;
                else if (Has(text, "visual studio"))
                    action = LocalAction.OpenVisualStudio;
                else if (Has(text, "bloc de notas") || Has(text, "notepad"))
                    action = LocalAction.OpenNotepad;
                else if (Has(text, "explorador de archivos") ||
                         Has(text, "explorador de windows"))
                    action = LocalAction.OpenFileExplorer;
                else if (Has(text, "navegador") || Has(text, "google chrome"))
                    action = LocalAction.OpenBrowser;
                else
                    return false;

                return true;
            }

            if (StartsWithAny(text,
                "siguiente cancion", "siguiente tema", "siguiente pista",
                "pasa a la siguiente", "pon la siguiente cancion",
                "pon la siguiente", "pasa de cancion", "pasa la cancion",
                "pasame la cancion", "salta esta cancion", "salta la cancion",
                "cambia de cancion", "pon otra cancion", "siguiente por favor",
                "puedes pasar de cancion", "puedes poner la siguiente"))
            {
                action = LocalAction.NextTrack;
                return true;
            }

            if (StartsWithAny(text,
                "cancion anterior", "tema anterior", "pista anterior",
                "pon la anterior", "vuelve a la cancion anterior",
                "vuelve a la anterior", "retrocede de cancion",
                "puedes poner la anterior", "ponme la anterior"))
            {
                action = LocalAction.PreviousTrack;
                return true;
            }

            if (StartsWithAny(text,
                "sube el volumen", "sube volumen", "aumenta el volumen"))
            {
                action = LocalAction.VolumeUp;
                return true;
            }

            if (StartsWithAny(text,
                "baja el volumen", "baja volumen", "reduce el volumen"))
            {
                action = LocalAction.VolumeDown;
                return true;
            }

            if (StartsWithAny(text,
                "silencia el pc", "silencia el ordenador", "quita el sonido",
                "activa el silencio", "quita el silencio"))
            {
                action = LocalAction.ToggleMute;
                return true;
            }

            if (StartsWithAny(text,
                "pausa la musica", "pausa la reproduccion", "para la musica",
                "deten la musica", "pausa youtube music", "para youtube music",
                "para la cancion", "puedes pausar la musica"))
            {
                action = LocalAction.PauseMedia;
                return true;
            }

            if (StartsWithAny(text,
                "pon la musica", "pon musica", "dale al play", "dale play",
                "puedes darle al play", "podrias darle al play",
                "reanuda la musica", "reanuda la reproduccion",
                "reproduce la musica", "reproduce musica", "reproduce youtube music",
                "continua la musica", "sigue con la musica"))
            {
                action = LocalAction.PlayMedia;
                return true;
            }

            if (StartsWithAny(text,
                "play pausa", "alternar reproduccion", "reproducir o pausar"))
            {
                action = LocalAction.ToggleMedia;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Los controles de música buscan específicamente la sesión de Chrome
        /// registrada en los controles multimedia de Windows. No tocan otras
        /// aplicaciones si Chrome no aparece o existen sesiones ambiguas.
        /// </summary>
        public async Task<string> ExecuteAsync(LocalAction action)
        {
            if (action is LocalAction.PlayMedia or LocalAction.PauseMedia
                or LocalAction.ToggleMedia or LocalAction.NextTrack
                or LocalAction.PreviousTrack)
            {
                return await ControlChromeAsync(action);
            }

            // Las acciones de apertura y volumen siguen como estaban.
            return Execute(action);
        }

        private static async Task<string> ControlChromeAsync(LocalAction action)
        {
            try
            {
                var manager =
                    await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                var chromeSessions = manager.GetSessions()
                    .Where(s => s.SourceAppUserModelId?.Contains(
                        "chrome", StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();

                if (chromeSessions.Count == 0)
                {
                    return "Chrome no está exponiendo ninguna sesión multimedia a Windows. " +
                           "Abre music.youtube.com y reproduce una canción manualmente " +
                           "para activar sus controles multimedia. No he enviado la orden a otro reproductor.";
                }

                // Un Chrome puede tener varias pestañas con audio. Priorizamos
                // una sola sesión que esté reproduciendo; si no hay una elección
                // inequívoca, NO controlamos otra pestaña por accidente.
                GlobalSystemMediaTransportControlsSession session;
                if (chromeSessions.Count == 1)
                {
                    session = chromeSessions[0];
                }
                else
                {
                    var playing = chromeSessions
                        .Where(s => s.GetPlaybackInfo()?.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        .ToList();

                    if (playing.Count != 1)
                    {
                        return "Windows detecta varias sesiones multimedia de Chrome y " +
                               "no puedo identificar con seguridad cuál es YouTube Music. " +
                               "Deja solo esa pestaña reproduciendo y vuelve a intentarlo.";
                    }

                    session = playing[0];
                }

                bool accepted = action switch
                {
                    LocalAction.PlayMedia => await session.TryPlayAsync(),
                    LocalAction.PauseMedia => await session.TryPauseAsync(),
                    LocalAction.ToggleMedia => await session.TryTogglePlayPauseAsync(),
                    LocalAction.NextTrack => await session.TrySkipNextAsync(),
                    LocalAction.PreviousTrack => await session.TrySkipPreviousAsync(),
                    _ => false
                };

                if (!accepted)
                    return "Chrome no ha aceptado esta orden multimedia. " +
                           "Comprueba que YouTube Music está abierto y tiene una canción cargada.";

                string operation = action switch
                {
                    LocalAction.PlayMedia => "reproducir",
                    LocalAction.PauseMedia => "pausar",
                    LocalAction.ToggleMedia => "alternar reproducción",
                    LocalAction.NextTrack => "pasar a la siguiente canción",
                    LocalAction.PreviousTrack => "volver a la canción anterior",
                    _ => "controlar la música"
                };

                return $"He enviado a Chrome la orden de {operation}.";
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or UnauthorizedAccessException or COMException
                or NotSupportedException)
            {
                return "No he podido acceder a los controles multimedia de Chrome: "
                    + ex.Message;
            }
        }

        public string Execute(LocalAction action)
        {
            try
            {
                switch (action)
                {
                    case LocalAction.OpenYoutubeMusic:
                        Open("https://music.youtube.com/");
                        return "He solicitado abrir YouTube Music en tu navegador.";

                    case LocalAction.OpenBrowser:
                        Open("https://www.google.com/");
                        return "He solicitado abrir el navegador predeterminado.";

                    case LocalAction.OpenFileExplorer:
                        Open("explorer.exe");
                        return "He solicitado abrir el Explorador de archivos.";

                    case LocalAction.OpenNotepad:
                        Open("notepad.exe");
                        return "He solicitado abrir el Bloc de notas.";

                    case LocalAction.OpenVisualStudio:
                        string? path = FindVisualStudio();
                        if (path == null)
                            return "No he encontrado el ejecutable de Visual Studio en las instalaciones habituales.";
                        Open(path);
                        return "He solicitado abrir Visual Studio.";

                    case LocalAction.ToggleMedia:
                        SendMediaKey(0xB3); // VK_MEDIA_PLAY_PAUSE
                        return "He enviado la tecla de reproducir o pausar. Si YouTube Music está abierto y acepta las teclas multimedia, debería responder. No puedo asegurar qué reproductor la recibió.";

                    case LocalAction.NextTrack:
                        SendMediaKey(0xB0); // VK_MEDIA_NEXT_TRACK
                        return "He enviado la orden multimedia de siguiente pista.";

                    case LocalAction.PreviousTrack:
                        SendMediaKey(0xB1); // VK_MEDIA_PREV_TRACK
                        return "He enviado la orden multimedia de pista anterior.";

                    case LocalAction.VolumeUp:
                        SendMediaKey(0xAF); // VK_VOLUME_UP
                        return "He enviado la orden de subir un paso el volumen.";

                    case LocalAction.VolumeDown:
                        SendMediaKey(0xAE); // VK_VOLUME_DOWN
                        return "He enviado la orden de bajar un paso el volumen.";

                    case LocalAction.ToggleMute:
                        SendMediaKey(0xAD); // VK_VOLUME_MUTE
                        return "He enviado la orden de alternar el silencio del sistema.";

                    default:
                        return "La acción solicitada no está disponible.";
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                        or System.IO.IOException)
            {
                return $"No he podido ejecutar esa acción: {ex.Message}";
            }
        }

        private static bool IsOpenCommand(string text) => StartsWithAny(text,
            "abre ", "abreme ", "inicia ", "ejecuta ", "lanza ",
            "puedes abrir ", "podrias abrir ", "me abres ",
            "quiero que abras ", "quiero abrir ");

        private static bool Has(string text, string phrase) =>
            (" " + text + " ").Contains(" " + phrase + " ", StringComparison.Ordinal);

        private static bool StartsWithAny(string text, params string[] prefixes) =>
            prefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal));

        private static string Normalize(string text)
        {
            string withoutAccents = new string(text.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .ToArray()).ToLowerInvariant();
            return Regex.Replace(withoutAccents, @"[^a-z0-9]+", " ").Trim();
        }

        private static void Open(string target)
        {
            var process = Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true
            });
            // ShellExecute puede devolver null cuando delega la acción en
            // una aplicación que ya estaba abierta; eso no implica fallo.
        }

        private static string? FindVisualStudio()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var roots = new[] { programFiles, programFilesX86 };

            // Su Visual Studio actual aparece como 18/Insiders en el registro.
            foreach (string root in roots)
            {
                string direct = Path.Combine(root, "Microsoft Visual Studio", "18",
                    "Insiders", "Common7", "IDE", "devenv.exe");
                if (File.Exists(direct)) return direct;
            }

            foreach (string root in roots)
            {
                string vsRoot = Path.Combine(root, "Microsoft Visual Studio");
                if (!Directory.Exists(vsRoot)) continue;
                foreach (string version in Directory.EnumerateDirectories(vsRoot).OrderByDescending(p => p))
                {
                    foreach (string edition in Directory.EnumerateDirectories(version))
                    {
                        string exe = Path.Combine(edition, "Common7", "IDE", "devenv.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
            }

            return null;
        }

        // Teclado multimedia mediante API Win32 SendInput, sin paquetes NuGet.
        // No ofrece una garantía de que la app deseada esté reproduciendo.
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT Mouse;
            [FieldOffset(0)] public KEYBDINPUT Keyboard;
            [FieldOffset(0)] public HARDWAREINPUT Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint Message;
            public ushort ParameterLow;
            public ushort ParameterHigh;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInput);

        private static void SendMediaKey(ushort key)
        {
            var inputs = new[]
            {
                new INPUT
                {
                    Type = INPUT_KEYBOARD,
                    Data = new InputUnion
                    {
                        Keyboard = new KEYBDINPUT { VirtualKey = key }
                    }
                },
                new INPUT
                {
                    Type = INPUT_KEYBOARD,
                    Data = new InputUnion
                    {
                        Keyboard = new KEYBDINPUT
                        {
                            VirtualKey = key,
                            Flags = KEYEVENTF_KEYUP
                        }
                    }
                }
            };

            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>())
                != (uint)inputs.Length)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows no aceptó la pulsación multimedia.");
            }
        }
    }
}
