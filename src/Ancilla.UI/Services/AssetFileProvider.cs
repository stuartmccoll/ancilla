using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Ancilla.UI.Services;

/// <summary>
/// Serves <see cref="AssetCache"/> images at <c>assets/{key}</c> to the desktop shell's web view,
/// which has no HTTP server: it asks a file provider for each URL instead. Like the web host's
/// <c>/assets</c> endpoint, it serves only what a signed-in session has already fetched, and never
/// needs the token itself.
/// </summary>
public sealed class AssetFileProvider(AssetCache assets) : IFileProvider
{
    private const string Prefix = "assets/";

    public IFileInfo GetFileInfo(string subpath)
    {
        var path = subpath.TrimStart('/');
        if (path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var key = path[Prefix.Length..];
            // Keys are cached as the API spelled them; the web view may hand the path over still escaped.
            if (assets.TryGet(key, out var a) || assets.TryGet(Uri.UnescapeDataString(key), out a))
                return new MemoryFile(Path.GetFileName(key), a.Bytes);
        }
        return new NotFoundFileInfo(subpath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private sealed class MemoryFile(string name, byte[] bytes) : IFileInfo
    {
        public bool Exists => true;
        public long Length => bytes.Length;
        public string? PhysicalPath => null;
        public string Name => name;
        // Cached images never change, so any fixed time will do.
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(bytes, writable: false);
    }
}
