using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.Services;

internal sealed record ServiceRuntimeRecord(int Version, string ServiceId, string Directory,
    ProcessIdentity[] Processes, string[] ConsoleJobs);

/// <summary>Stores only process identities; application output and launch configuration never enter this store.</summary>
internal static class ServiceRuntimeStore
{
    private static string Normalize(string directory) => Path.GetFullPath(directory).TrimEnd('\\', '/').ToUpperInvariant();

    private static string PathFor(string serviceId, string directory)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serviceId + "\0" + Normalize(directory))));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FullStackLauncher", "service-runtime", key + ".json");
    }

    internal static ServiceRuntimeRecord? Load(string serviceId, string directory)
    {
        var path = PathFor(serviceId, directory);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidOperationException("The saved service process identities are too large to inspect safely.");
        var record = JsonSerializer.Deserialize<ServiceRuntimeRecord>(File.ReadAllText(path));
        if (record is null || record.Version != 1 || record.ServiceId != serviceId || record.Directory != Normalize(directory)
            || record.Processes is null || record.ConsoleJobs is null || record.Processes.Length > 4096 || record.ConsoleJobs.Length > 16
            || record.Processes.Any(p => p is null || p.Id <= 4 || p.StartedUtcTicks <= 0)
            || record.ConsoleJobs.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("The saved service process identities are invalid. The launcher did not adopt them.");
        return record;
    }

    internal static void Save(string serviceId, string directory, IEnumerable<ProcessIdentity> processes, IEnumerable<string> jobs)
    {
        var path = PathFor(serviceId, directory);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var record = new ServiceRuntimeRecord(1, serviceId, Normalize(directory), processes.Distinct().ToArray(), jobs.Distinct().ToArray());
        if (record.Processes.Length > 4096 || record.ConsoleJobs.Length > 16)
            throw new InvalidOperationException("Too many service processes remain to save a safe recovery record. The launcher remains open.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
