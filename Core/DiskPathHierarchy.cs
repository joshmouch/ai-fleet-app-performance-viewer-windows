#nullable enable

public static class DiskPathHierarchy
{
    public static bool Contains(string folder, string path) =>
        path.StartsWith(folder, folder.StartsWith('/') ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    public static string? ChildFolder(string path, string? parent)
    {
        if (string.IsNullOrEmpty(path) || parent != null && !Contains(parent, path)) return null;
        char separator = path.StartsWith('/') ? '/' : '\\';
        int start = parent?.Length ?? RootLength(path, separator);
        int next = path.IndexOf(separator, start);
        return next > start ? path[..(next + 1)] : null;
    }

    private static int RootLength(string path, char separator)
    {
        if (separator == '/' || path.StartsWith('\\') && !path.StartsWith(@"\\"))
            return 1;
        if (path.Length >= 3 && path[1] == ':' && path[2] == separator) return 3;
        if (!path.StartsWith(@"\\") || path.Length <= 2) return 0;

        // Skip the server and share in UNC paths, and the volume device prefix.
        int serverEnd = path.IndexOf(separator, 2);
        if (serverEnd < 0) return path.Length;
        int shareEnd = path.IndexOf(separator, serverEnd + 1);
        return shareEnd < 0 ? path.Length : shareEnd + 1;
    }
}

public sealed record DiskPathMeasurement(string Host, string Environment, FileIoKey File,
    DiskGuestRate? Physical, DiskGuestRate? Guest)
{
    public static (DiskGuestRate? Physical, DiskGuestRate? Guest) Sum(IEnumerable<DiskPathMeasurement> files)
    {
        DiskGuestRate? physical = null, guest = null;
        foreach (var file in files)
        {
            if (file.Physical is { } hostRate) physical = physical.GetValueOrDefault() + hostRate;
            if (file.Guest is { } guestRate) guest = guest.GetValueOrDefault() + guestRate;
        }
        return (physical, guest);
    }
}
