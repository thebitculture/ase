using System.Reflection;

namespace ASE;

public static class BuildCredentials
{
    private static string Read(string key) =>
        typeof(BuildCredentials).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? string.Empty;

    public static string DevId => Read("SsDevId");
    public static string SsDevApp => Read("SsDevApp");
    public static string DevPassword => Read("SsDevPassword");
    public static string SsDevDebugPassword => Read("SsDevDebugPassword");
    /// <summary>Key material for StringOfuscator. Not a credential itself.</summary>
    public static string CryptoSeed => Read("CryptoSeed");
            
    /// <summary>False on fork and local builds, where no secrets were injected.</summary>
    public static bool IsConfigured =>
        !string.IsNullOrEmpty(DevId) && !string.IsNullOrEmpty(DevPassword);
}
