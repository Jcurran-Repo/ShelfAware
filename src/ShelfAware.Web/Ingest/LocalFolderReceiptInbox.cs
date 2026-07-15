using ShelfAware.Core.Ingest;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Ingest;

/// <summary>
/// <see cref="IReceiptInbox"/> backed by a local filesystem folder (the configured "receipt drop folder").
/// The id is the file name — stable enough to detect already-imported files. Swap this out for an
/// Azure Blob / cloud-drive impl once the app is deployed; the import logic doesn't change.
/// </summary>
public class LocalFolderReceiptInbox(IAppSettings settings) : IReceiptInbox
{
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var folder = await FolderAsync(cancellationToken);
        return folder is not null && Directory.Exists(folder);
    }

    public async Task<IReadOnlyList<InboxItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var folder = await FolderAsync(cancellationToken);
        if (folder is null || !Directory.Exists(folder)) return [];

        return Directory.EnumerateFiles(folder)
            .Where(ReceiptMediaTypes.IsSupported)
            .Select(path => Path.GetFileName(path))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new InboxItem(name, name, ReceiptMediaTypes.ForPath(name)))
            .ToList();
    }

    public async Task<byte[]> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        var folder = await FolderAsync(cancellationToken)
            ?? throw new InvalidOperationException("No receipt folder configured.");
        // Guard against path traversal — id must be a bare file name within the folder.
        var safe = Path.GetFileName(id);
        if (!string.Equals(safe, id, StringComparison.Ordinal))
            throw new InvalidOperationException($"Invalid inbox id '{id}'.");
        return await File.ReadAllBytesAsync(Path.Combine(folder, safe), cancellationToken);
    }

    private async Task<string?> FolderAsync(CancellationToken ct)
    {
        var folder = (await settings.GetAsync(SettingKeys.ReceiptFolder, ct))?.Trim();
        return string.IsNullOrEmpty(folder) ? null : folder;
    }
}
