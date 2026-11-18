using Microsoft.Extensions.Caching.Memory;

namespace C0302_HoangThai.Service.S0302;

public sealed class SpreadsheetDownloads(IMemoryCache cache)
{
    public static string CreatePath(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "C0302-Spreadsheets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    public string Register(string path)
    {
        string id = Guid.NewGuid().ToString("N");
        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
        options.RegisterPostEvictionCallback((_, value, _, _) =>
        {
            if (value is not string expiredPath)
                return;
            try
            {
                File.Delete(expiredPath);
                Directory.Delete(Path.GetDirectoryName(expiredPath)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        });
        cache.Set(id, path, options);
        return id;
    }

    public string? Find(string? id) => Guid.TryParseExact(id, "N", out _) ? cache.Get<string>(id!) : null;
}
