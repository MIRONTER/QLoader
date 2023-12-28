using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Serilog;
using SQLitePCL;

namespace QSideloader.Utilities;

public static class OculusTokenExtractor
{
    /// <summary>
    ///     Extracts the Oculus token from the Oculus app database.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="PlatformNotSupportedException">Thrown if the current platform is not Windows.</exception>
    /// <exception cref="Exception">Thrown if could not extract the token.</exception>
    private static string ExtractToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Oculus token extraction is only supported on Windows.");
        }
        
        Batteries_V2.Init();

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var oculusDbPath = Path.Combine(appDataPath, @"Oculus\Sessions\_oaf\data.sqlite");

        if (!File.Exists(oculusDbPath))
        {
            throw new Exception("Oculus app db not found. Please run Oculus app and login before running this tool.");
        }

        // Copy the db file as it's locked by Oculus service
        var tempDbPath = Path.Combine(Path.GetDirectoryName(oculusDbPath)!, "_data.sqlite");
        File.Copy(oculusDbPath, tempDbPath, true);

        using var oculusDb = new SqliteConnection($"Data Source={tempDbPath}");

        oculusDb.Open();

        using var command = oculusDb.CreateCommand();
        command.CommandText = "SELECT value FROM 'Objects' WHERE hashkey = '__OAF_OFFLINE_DATA_KEY__'";

        using var reader = command.ExecuteReader();
        if (!reader.HasRows)
        {
            throw new Exception(
                "Could not find offline data in Oculus app db. Try restarting Oculus app and make sure you are logged in.");
        }
        reader.Read();
        
        var offlineDataByteArray = (byte[]) reader["value"];
        var offlineDataString = Encoding.UTF8.GetString(offlineDataByteArray);

        var tokenStartIndex = offlineDataString.IndexOf("last_valid_auth_token", StringComparison.Ordinal);
        if (tokenStartIndex == -1)
        {
            throw new Exception("Could not find last_valid_auth_token in offline data.");
        }
        tokenStartIndex += 31;

        var tokenLength =
            offlineDataString[tokenStartIndex..].IndexOf("last_valid_auth_token_type", StringComparison.Ordinal) > -1
                ? offlineDataString[tokenStartIndex..].IndexOf("last_valid_auth_token_type", StringComparison.Ordinal) -
                  4
                : offlineDataString.IndexOf("last_valid_fb_access_token", StringComparison.Ordinal);
        
        if (tokenLength == -1)
        {
            throw new Exception("Could not find last_valid_auth_token_type or last_valid_fb_access_token in offline data.");
        }
        return offlineDataString[tokenStartIndex..(tokenStartIndex + tokenLength)];
    }

    public static async Task ExtractAndUploadTokenAsync(string? donorName = null)
    {
        Log.Information("Extracting Oculus token...");
        var token = ExtractToken();
        Log.Information("Got token, length: {Length}", token.Length);
        await ApiClient.UploadOculusTokenAsync(token, donorName);
    }
    
    public static bool IsOculusDbAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var oculusDbPath = Path.Combine(appDataPath, @"Oculus\Sessions\_oaf\data.sqlite");

        return File.Exists(oculusDbPath);
    }
}