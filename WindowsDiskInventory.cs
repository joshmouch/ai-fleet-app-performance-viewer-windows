using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

internal sealed class DiskIdentity
{
    public readonly string Name;
    public readonly string Kind;
    public readonly bool IsStorageSpaces;
    public string[] AccessPaths;
    public DiskVolume[] Volumes;

    public DiskIdentity(string name, string kind, bool isStorageSpaces = false)
    {
        Name = name;
        Kind = kind;
        IsStorageSpaces = isStorageSpaces;
    }
}

internal static class WindowsDiskInventory
{
    private const int StorageSpacesBusType = 16;

    internal static Dictionary<int, DiskIdentity> LoadDiskIdentities()
    {
        const string storageNamespace = @"root\Microsoft\Windows\Storage";
        var identities = new Dictionary<int, DiskIdentity>();
        using (var search = new ManagementObjectSearcher(storageNamespace, "SELECT Number, FriendlyName, BusType FROM MSFT_Disk"))
        using (var disks = search.Get())
        {
            foreach (ManagementObject disk in disks)
                using (disk)
                {
                    var name = disk["FriendlyName"] as string;
                    if (disk["Number"] != null)
                    {
                        int busType = Convert.ToInt32(disk["BusType"]);
                        identities[Convert.ToInt32(disk["Number"])] =
                            new DiskIdentity(name, DiskKind(busType), busType == StorageSpacesBusType);
                    }
                }
        }

        try
        {
            var localNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var search = new ManagementObjectSearcher(storageNamespace, "SELECT * FROM MSFT_StorageNode"))
            using (var nodes = search.Get())
            {
                foreach (ManagementObject node in nodes)
                    using (node)
                    {
                        string name = node["Name"] as string;
                        if (name != null && name.Split('.')[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                            localNodes.Add(node.Path.RelativePath);
                    }
            }
            using (var search = new ManagementObjectSearcher(storageNamespace,
                "SELECT DiskNumber, PhysicalDisk, StorageNode FROM MSFT_StorageNodeToPhysicalDisk"))
            using (var links = search.Get())
            {
                foreach (ManagementObject link in links)
                    using (link)
                    {
                        var nodePath = link["StorageNode"] as string;
                        if (link["DiskNumber"] == null || nodePath == null ||
                            !localNodes.Contains(new ManagementPath(nodePath).RelativePath))
                            continue;
                        int number = Convert.ToInt32(link["DiskNumber"]);
                        var path = link["PhysicalDisk"] as string;
                        if (identities.ContainsKey(number) || string.IsNullOrWhiteSpace(path))
                            continue;
                        using (var disk = new ManagementObject(path))
                        {
                            var model = disk["FriendlyName"] as string;
                            identities.Add(number, new DiskIdentity(model, "Physical drive"));
                        }
                    }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (COMException) { }

        try
        {
            var volumesByDisk = new Dictionary<int, List<DiskVolume>>();
            using (var search = new ManagementObjectSearcher(storageNamespace, "SELECT DiskNumber, AccessPaths FROM MSFT_Partition"))
            using (var partitions = search.Get())
            {
                foreach (ManagementObject partition in partitions)
                    using (partition)
                    {
                        var accessPaths = partition["AccessPaths"] as string[];
                        if (partition["DiskNumber"] == null || accessPaths == null)
                            continue;

                        var paths = accessPaths.Select(NormalizeAccessPath).Where(path => path != null)
                            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length)
                            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
                        int number = Convert.ToInt32(partition["DiskNumber"]);
                        var volume = DiskVolume.Create(number, paths, accessPaths);
                        if (volume.DisplayRoot == null)
                            continue;

                        List<DiskVolume> volumes;
                        if (!volumesByDisk.TryGetValue(number, out volumes))
                        {
                            volumes = new List<DiskVolume>();
                            volumesByDisk.Add(number, volumes);
                        }
                        volumes.Add(volume);
                    }
            }

            foreach (var identity in identities.Values)
                identity.AccessPaths = new string[0];
            foreach (var entry in volumesByDisk)
            {
                DiskIdentity identity;
                if (!identities.TryGetValue(entry.Key, out identity))
                {
                    identity = new DiskIdentity(null, "Windows disk");
                    identities.Add(entry.Key, identity);
                }
                identity.Volumes = entry.Value.ToArray();
                identity.AccessPaths = entry.Value.SelectMany(volume => volume.Paths)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (COMException) { }

        return identities;
    }

    private static string NormalizeAccessPath(string path)
    {
        if (path == null)
            return null;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.Substring(4);
        return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\' ? path : null;
    }

    private static string DiskKind(int busType)
    {
        // MSFT_Disk reports values from STORAGE_BUS_TYPE.
        switch (busType)
        {
            case StorageSpacesBusType: return "Storage Spaces virtual disk";
            case 14:
            case 15: return "Virtual disk";
            case 17: return "NVMe drive";
            case 7: return "USB drive";
            default: return "Windows disk";
        }
    }
}
