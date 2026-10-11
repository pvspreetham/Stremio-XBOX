using System;
using System.IO;
using Windows.Storage;

namespace StremioXbox.Core
{
    /// <summary>
    /// Writes log lines to LocalState\session.log so they survive a crash or termination.
    /// On start, the previous session's file is kept as previous.log.
    /// Readable through Device Portal: File explorer -> LocalAppData -> StremioXbox_* -> LocalState.
    /// </summary>
    public static class FileLog
    {
        private static readonly object Gate = new object();
        private static StreamWriter _writer;

        /// <summary>Opens session.log, keeping the last session as previous.log.</summary>
        public static void Start()
        {
            lock (Gate)
            {
                if (_writer != null)
                {
                    return;
                }

                try
                {
                    var folder = ApplicationData.Current.LocalFolder.Path;
                    var current = Path.Combine(folder, "session.log");
                    var previous = Path.Combine(folder, "previous.log");
                    if (File.Exists(current))
                    {
                        File.Copy(current, previous, overwrite: true);
                    }

                    _writer = new StreamWriter(new FileStream(current, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("FileLog start failed: " + ex.Message);
                }
            }
        }

        /// <summary>Appends one timestamped line.</summary>
        public static void Write(string level, string text)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + level.ToUpperInvariant() + " " + text;
            System.Diagnostics.Debug.WriteLine(line);
            lock (Gate)
            {
                try
                {
                    _writer?.WriteLine(line);
                }
                catch (Exception)
                {
                    // Logging must never take the app down.
                }
            }
        }
    }
}
