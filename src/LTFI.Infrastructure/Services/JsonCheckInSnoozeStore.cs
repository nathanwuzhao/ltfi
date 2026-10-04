using System;
using System.IO;
using System.Text.Json;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;

namespace LTFI.Infrastructure.Services;

/// <summary>
/// Keeps the check-in snooze state in a tiny JSON file (default <c>%AppData%/LTFI/checkin-snooze.json</c>)
/// so snoozes survive an app restart without a schema change. A missing or corrupt file reads as
/// "no snoozes taken".
/// </summary>
public sealed class JsonCheckInSnoozeStore(string filePath) : ICheckInSnoozeStore
{
    private readonly string _filePath = filePath;

    public CheckInSnoozeState? Load()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize<CheckInSnoozeState>(File.ReadAllText(_filePath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(CheckInSnoozeState state) =>
        File.WriteAllText(_filePath, JsonSerializer.Serialize(state));
}
