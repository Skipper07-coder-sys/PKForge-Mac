using PKForge.Domain;

namespace PKForge.App;

/// <summary>
/// Save and folder I/O by absolute path. The Mac build is unsandboxed, so a picked path stays
/// readable and writable across launches, just as it is for the emulators that own the files.
/// </summary>
public sealed class MacFileAccess : ISaveFileAccess, IFolderFileAccess
{
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) =>
        await File.ReadAllBytesAsync(documentId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Writes beside the target, flushes to disk, then renames over it: a crash or full disk
    /// leaves either the old save or the new one, never a torn file.
    /// </summary>
    public async ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        // Follow a symlink so the save updates its target instead of replacing the link.
        var info = new FileInfo(documentId);
        var target = info.LinkTarget is null ? documentId : (info.ResolveLinkTarget(true)?.FullName ?? documentId);
        var directory = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("The save path has no folder.", nameof(documentId));
        var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.pkforge-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(target))
            {
                // Best effort: the new file should keep the old one's permissions.
                try { File.SetUnixFileMode(temporary, File.GetUnixFileMode(target)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            File.Move(temporary, target, overwrite: true);
        }
#if IOS
        catch (UnauthorizedAccessException) when (!File.Exists(temporary))
        {
            // A save picked on its own grants that one file: iOS refuses any new file beside it.
            // Overwrite it in place; the restore point taken before every write covers a torn file.
            await using var output = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            output.SetLength(bytes.Length);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
#endif
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public ValueTask<IReadOnlyList<PickedDocument>> ListFilesAsync(string treeId, CancellationToken cancellationToken = default) =>
        new(Task.Run<IReadOnlyList<PickedDocument>>(() => Directory.EnumerateFiles(treeId)
            .Where(path => !Path.GetFileName(path).StartsWith('.'))
            .Select(path => new PickedDocument(path, Path.GetFileName(path)))
            .ToList(), cancellationToken));

    public ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(string documentId, CancellationToken cancellationToken = default) =>
        ReadAsync(documentId, cancellationToken);

    public ValueTask WriteFileAsync(string treeId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("The file name is empty.", nameof(fileName));
        if (fileName != Path.GetFileName(fileName) || fileName is "." or "..")
            throw new ArgumentException($"'{fileName}' is not a plain file name.", nameof(fileName));
        return WriteAtomicallyAsync(Path.Combine(treeId, fileName), bytes, cancellationToken);
    }
}
