using System;
using System.IO;
using BepInEx;

namespace ImprovedVillagerInventoryCleanup.Diagnostics;

internal static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static StreamWriter _writer;
    internal static string OutputPath { get; private set; }

    internal static void Initialize()
    {
        lock (Sync)
        {
            var directory = Path.Combine(Paths.ConfigPath, "ImprovedVillagerInventoryCleanup");
            Directory.CreateDirectory(directory);
            OutputPath = Path.Combine(directory, "diagnostic-latest.log");
            _writer?.Dispose();
            _writer = new StreamWriter(OutputPath, append: false) { AutoFlush = true };
            WriteUnsafe("session_started", $"utc={DateTime.UtcNow:O}");
        }
    }

    internal static void Write(string eventName, string details)
    {
        try
        {
            lock (Sync) { WriteUnsafe(eventName, details); }
            Plugin.Log?.LogInfo($"IVIC_DIAG event={eventName} {details}");
        }
        catch { }
    }

    internal static void WriteException(string eventName, Exception exception) =>
        Write(eventName, $"exception={Quote(exception.ToString())}");

    internal static string Quote(string value)
    {
        if (value == null) return "<null>";
        return '"' + value.Replace("\\", "\\\\").Replace("\r", "\\r")
            .Replace("\n", "\\n").Replace("\"", "\\\"") + '"';
    }

    internal static void Shutdown()
    {
        lock (Sync)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void WriteUnsafe(string eventName, string details) =>
        _writer?.WriteLine($"utc={DateTime.UtcNow:O} realtime={UnityEngine.Time.realtimeSinceStartup:0.000} event={eventName} {details}");
}
