namespace WkMcp;

/// <summary>
/// Pure path-containment helpers shared by the destructive file tools. Kept in
/// one place (and free of I/O) so the guards are unit-testable in isolation —
/// a missing guard here is a path-traversal / arbitrary-write hole.
/// </summary>
internal static class PathSafety
{
    /// <summary>
    /// Resolves <paramref name="relative"/> against <paramref name="root"/> and verifies the
    /// result stays inside <paramref name="root"/>. Returns <c>false</c> when
    /// <paramref name="relative"/> is rooted (which would make <see cref="Path.Combine(string,string)"/>
    /// discard the root entirely) or escapes the root via <c>..</c> segments.
    /// On success <paramref name="full"/> is the absolute, normalized destination.
    /// </summary>
    internal static bool TryResolveInside(string root, string relative, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative))
            return false;

        // A rooted "relative" (absolute path or drive-qualified) would let
        // Path.Combine throw away the root — the classic arbitrary-write vector.
        if (Path.IsPathRooted(relative))
            return false;

        var rootFull = Path.GetFullPath(root);
        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(rootFull, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var rootWithSep = rootFull
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        // The destination itself must sit strictly under root/.
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            return false;

        full = combined;
        return true;
    }

    /// <summary>
    /// True when <paramref name="name"/> is a bare file name — no directory part,
    /// no separators, no <c>..</c>, no characters illegal in a file name. Used by
    /// tools that move/delete by name within a fixed directory.
    /// </summary>
    internal static bool IsBareFileName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && Path.GetFileName(name) == name;
}
