#nullable enable

public readonly record struct DiskPathFolder(string Host, string Environment, string Path)
{
    public string Id => string.Join("/", Uri.EscapeDataString(Host.ToUpperInvariant()),
        Uri.EscapeDataString(Environment), Uri.EscapeDataString(IsUnix(Path) ? Path : Path.ToUpperInvariant()));
    public string Name => new FileIoKey(0, "", Path.TrimEnd(IsUnix(Path) ? '/' : '\\')).Filename;

    public bool Equals(DiskPathFolder other) => StringComparer.OrdinalIgnoreCase.Equals(Host, other.Host) &&
        Environment == other.Environment && PathComparer.Equals(Path, other.Path);

    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Host),
        Environment, PathComparer.GetHashCode(Path));

    private StringComparer PathComparer => IsUnix(Path) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public bool Includes(DiskPathMeasurement file) =>
        string.Equals(Host, file.Host, StringComparison.OrdinalIgnoreCase) && Environment == file.Environment &&
        file.File.Path.StartsWith(Path, IsUnix(Path) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    internal static DiskPathFolder? Child(DiskPathMeasurement file, DiskPathFolder? parent)
    {
        var path = file.File.Path;
        if (string.IsNullOrEmpty(path)) return null;
        char separator = IsUnix(path) ? '/' : '\\';
        int start = parent?.Path.Length ?? RootLength(path, separator);
        int next = path.IndexOf(separator, start);
        return next > start ? new(file.Host, file.Environment, path[..(next + 1)]) : null;
    }

    private static bool IsUnix(string path) => path.StartsWith('/');

    private static int RootLength(string path, char separator)
    {
        if (separator == '/' || path.StartsWith('\\') && !path.StartsWith(@"\\"))
            return 1;
        if (path.Length >= 3 && path[1] == ':' && path[2] == separator) return 3;
        if (!path.StartsWith(@"\\") || path.Length <= 2) return 0;

        // A network share (and the \\?\Volume{…}\ prefix) is a root, like a drive.
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

public static class DiskPathHierarchy
{
    // At each level, a measurement belongs to one child folder or remains a
    // direct file. Unnamed requests stay as rows at the top of their scope.
    public static IEnumerable<(DiskPathFolder? Folder, DiskPathMeasurement Measurement)> Rows(
        IEnumerable<DiskPathMeasurement> files, DiskPathFolder? parent)
    {
        var groups = new Dictionary<DiskPathFolder, List<DiskPathMeasurement>>();
        foreach (var file in files)
        {
            if (parent is { } scope && !scope.Includes(file)) continue;
            if (DiskPathFolder.Child(file, parent) is not { } child)
            {
                yield return (null, file);
                continue;
            }
            if (!groups.TryGetValue(child, out var group)) groups.Add(child, group = []);
            group.Add(file);
        }
        foreach (var (folder, members) in groups)
        {
            var (physical, guest) = DiskPathMeasurement.Sum(members);
            yield return (folder, new(folder.Host, folder.Environment, new(0, "", folder.Path), physical, guest));
        }
    }
}
