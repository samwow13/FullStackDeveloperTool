using System.Net.Http;
using System.Text.Json;

namespace FullStackLauncher.Services;

/// <summary>
/// Optional, bounded read of a loopback API's database diagnostic. A successful response
/// reports the database name after that API's own read-only connectivity check.
/// </summary>
internal static class ApiDatabaseHealthProbe
{
    private const int MaxResponseBytes = 4096;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        UseProxy = false
    }) { Timeout = TimeSpan.FromSeconds(3) };

    public static Uri? HealthUriFor(string? activeUrl)
    {
        if (!Uri.TryCreate(activeUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || !endpoint.IsLoopback ||
            !string.IsNullOrEmpty(endpoint.UserInfo)) return null;
        return new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/health/database");
    }

    public static async Task<string?> ReadVerifiedNameAsync(Uri healthUri)
    {
        if (HealthUriFor(healthUri.AbsoluteUri) != healthUri) return null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUri);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK ||
                response.Content.Headers.ContentLength is > MaxResponseBytes) return null;

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var bytes = new byte[MaxResponseBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await body.ReadAsync(bytes.AsMemory(count), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count is 0 or > MaxResponseBytes) return null;
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetSingleProperty(root, "status", out var status) ||
                status.ValueKind != JsonValueKind.String ||
                !string.Equals(status.GetString(), "Healthy", StringComparison.OrdinalIgnoreCase) ||
                !TryGetSingleProperty(root, "usersTableAccessible", out var table) ||
                table.ValueKind != JsonValueKind.True ||
                !TryGetSingleProperty(root, "database", out var database) ||
                database.ValueKind != JsonValueKind.String) return null;
            return ApiDatabaseIdentifier.SafeName(database.GetString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A diagnostic failure never changes service health or reveals response contents.
            return null;
        }
    }

    private static bool TryGetSingleProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) return false;
            value = property.Value;
            found = true;
        }
        return found;
    }
}
