namespace NekcaApps.Core;

/// <summary>The app list: online (so new apps appear without updating the launcher), else the last one read, else built in.</summary>
public sealed class CatalogSource(HttpClient http, string cacheFolder)
{
    public async Task<(Catalog Catalog, string? Problem)> GetAsync(CancellationToken ct = default)
    {
        var file = Path.Combine(cacheFolder, "apps.json");
        try
        {
            var json = await http.GetStringAsync(Catalog.OnlineUrl, ct);
            var c = Catalog.Parse(json);
            if (c.Apps.Count > 0)
            {
                try { Directory.CreateDirectory(cacheFolder); await File.WriteAllTextAsync(file, json, ct); } catch (IOException) { }
                return (c, null);
            }
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) && !ct.IsCancellationRequested) { }
        try
        {
            if (File.Exists(file) && Catalog.Parse(await File.ReadAllTextAsync(file, ct)) is { Apps.Count: > 0 } saved)
                return (saved, "Showing the app list from the last time GitHub could be reached.");
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        return (Catalog.BuiltIn(), "Showing the built-in app list (GitHub couldn't be reached).");
    }
}
