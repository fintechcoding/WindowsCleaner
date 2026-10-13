using System.Text.Json;

namespace Cleaner.Core;

public static class Product
{
    public const string ServiceName = "WindowsCleaner";
    public const string PipeName = "WindowsCleaner.v1";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsCleaner");
}

public sealed record Request(string Command);
public sealed record CleanupResult(int Deleted = 0, int Skipped = 0, string[]? Errors = null);
public sealed record Status(bool Running, bool Busy, bool TelemetryBlocked, string InstanceId, long Cycle,
    DateTimeOffset? LastRun, DateTimeOffset? NextRun, CleanupResult? Result, string? Error = null);
public sealed class Settings
{
    public bool Running { get; set; } = true;
    public bool TelemetryBlocked { get; set; }
}

public static class JsonFiles
{
    public static T Read<T>(string path, Func<T> fallback) => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid settings file.")
        : fallback();

    public static void Write<T>(string path, T value)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}

public sealed class ResultBuilder
{
    public int Deleted;
    public int Skipped;
    private readonly List<string> errors = [];
    public void Skip(string message) { Skipped++; if (errors.Count < 20) errors.Add(message); }
    public void Add(CleanupResult result)
    {
        Deleted += result.Deleted;
        Skipped += result.Skipped;
        foreach (var error in result.Errors ?? []) if (errors.Count < 20) errors.Add(error);
    }
    public CleanupResult Build() => new(Deleted, Skipped, errors.ToArray());
}
