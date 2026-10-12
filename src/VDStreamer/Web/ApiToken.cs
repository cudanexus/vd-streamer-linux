using System;
using System.IO;
using System.Security.Cryptography;
using VDStreamer.Vendor;

namespace VDStreamer.Web;

/// <summary>Per-user secret protecting the local control API (stored with 0600 permissions).</summary>
internal static class ApiToken
{
    private static string _value;

    public static string FilePath => Path.Combine(Paths.ConfigDir, "api-token");

    public static string Value
    {
        get
        {
            if (_value != null)
            {
                return _value;
            }
            string file = FilePath;
            if (File.Exists(file))
            {
                _value = File.ReadAllText(file).Trim();
            }
            if (string.IsNullOrEmpty(_value))
            {
                _value = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                File.WriteAllText(file, _value);
            }
            try
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
            }
            return _value;
        }
    }
}
