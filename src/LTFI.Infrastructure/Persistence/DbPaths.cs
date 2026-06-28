using System;
using System.IO;

namespace LTFI.Infrastructure.Persistence;

/// <summary>
/// Resolves the deterministic, user-local on-disk location of LTFI data (plan §1.4).
/// Production: <c>%AppData%/LTFI/ltfi.db</c>. The folder is created on demand.
/// </summary>
public static class DbPaths
{
    public const string AppFolderName = "LTFI";
    public const string DatabaseFileName = "ltfi.db";

    /// <summary><c>%AppData%/LTFI</c> (created if missing). Also holds logs.</summary>
    public static string AppDataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolderName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DatabaseFilePath => Path.Combine(AppDataDirectory, DatabaseFileName);

    public static string LogDirectory
    {
        get
        {
            var dir = Path.Combine(AppDataDirectory, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string GetConnectionString() => $"Data Source={DatabaseFilePath}";
}
