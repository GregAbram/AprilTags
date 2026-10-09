using System;
using System.IO;
using UnityEngine;

// Opt-in session log: after Begin(), appends the app's own log lines - those
// starting with a [Component] tag, as this package's do - plus every warning
// and error, with timestamps, to a file in persistentDataPath. A device's
// system log is overwritten within minutes; this keeps a whole session. The
// previous session's file is kept alongside as <name>.prev.log. Call Begin()
// early, e.g. from a [RuntimeInitializeOnLoadMethod(BeforeSceneLoad)] method.
// Quest: adb pull /sdcard/Android/data/<app id>/files/<name>.log
// iPhone: devicectl device copy from ... --domain-type appDataContainer, or
// Files/Finder if the app enables file sharing.
public static class SessionLogFile
{
    private static readonly object Gate = new();
    private static StreamWriter writer;

    public static string Path { get; private set; }

    public static void Begin(string fileName = "session.log")
    {
        if (writer != null || Application.isEditor)
        {
            return;
        }
        try
        {
            Path = System.IO.Path.Combine(Application.persistentDataPath, fileName);
            var previous = System.IO.Path.ChangeExtension(Path, ".prev.log");
            if (File.Exists(Path))
            {
                File.Copy(Path, previous, true);
            }
            writer = new StreamWriter(Path, false) { AutoFlush = true };
            writer.WriteLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {Application.productName} {Application.version} on {SystemInfo.deviceModel} ({SystemInfo.operatingSystem})");
            Application.logMessageReceivedThreaded += OnLog;
            Application.quitting += () =>
            {
                lock (Gate)
                {
                    writer?.Dispose();
                    writer = null;
                }
            };
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SessionLogFile] Can't write the session log: {e.Message}");
        }
    }

    private static void OnLog(string message, string stackTrace, LogType type)
    {
        var important = type is LogType.Warning or LogType.Error or LogType.Exception or LogType.Assert;
        if (!important && !message.StartsWith("["))
        {
            return;
        }
        lock (Gate)
        {
            if (writer == null)
            {
                return;
            }
            writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {(important ? type.ToString().ToUpperInvariant() + " " : "")}{message}");
            if (type is LogType.Error or LogType.Exception && !string.IsNullOrEmpty(stackTrace))
            {
                writer.WriteLine(stackTrace.TrimEnd());
            }
        }
    }
}
