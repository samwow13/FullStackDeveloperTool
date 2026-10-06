using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace FullStackLauncher.Services;

internal enum SwaggerUiAvailability { NotApplicable, ApiUnavailable, Checking, Unavailable, Ready }

internal sealed record SwaggerUiStatus(SwaggerUiAvailability Availability, string Message, string? UiUrl = null);

/// <summary>Verifies the configured Swagger UI without scanning routes or trusting arbitrary HTTP success pages.</summary>
internal static class SwaggerUiProbe
{
    private const int MaximumHtmlBytes = 128 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        MaxConnectionsPerServer = 2
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly Regex Scripts = new(@"<script\b(?<attributes>[^>]*)>(?<script>.*?)</script\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ScriptSource = new(@"\bsrc\s*=\s*[""'](?<source>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex HtmlComments = new(@"<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex InitializeSwagger = new(@"\b(?:SwaggerUIBundle|SwaggerUI)\s*\(",
        RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SwaggerContainer = new(@"<(?:div|section)\b[^>]*\bid\s*=\s*[""']swagger-ui[""']",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    internal static bool IsSwaggerUiUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => Uri.UnescapeDataString(segment).Equals("swagger", StringComparison.OrdinalIgnoreCase));

    internal static async Task<SwaggerUiStatus> ProbeAsync(Uri configuredUi, CancellationToken cancellationToken)
    {
        var originalUrl = configuredUi.AbsoluteUri;
        if (!configuredUi.IsLoopback || !string.IsNullOrEmpty(configuredUi.UserInfo) ||
            configuredUi.Scheme is not ("http" or "https"))
            return Unavailable("Swagger requires a valid local HTTP or HTTPS address.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var token = timeout.Token;
        var current = configuredUi;
        try
        {
            for (var redirect = 0; redirect <= 2; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Accept.ParseAdd("text/html");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (redirect == 2 || location is null ||
                        !Uri.TryCreate(current, location, out var target) || !IsCanonicalUiRedirect(configuredUi, target))
                        return Unavailable("Swagger was not detected: the configured address redirects away from its Swagger UI. It may be unavailable or require sign-in.");
                    current = target;
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return Unavailable($"Swagger returned HTTP {status}. Check Swagger access for the current API environment.");
                if (status >= 500)
                    return Unavailable($"Swagger could not be verified because the API returned HTTP {status}. Check the API output and retry after the server error is resolved.");
                if (response.StatusCode != HttpStatusCode.OK)
                    return Unavailable($"Swagger was not detected at {originalUrl} (HTTP {status}). Swagger may not be installed or enabled for the current API environment.");
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null && !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                    return Unavailable($"Swagger UI was not detected at {originalUrl}. The address returned a different content type; check the configured Swagger path.");
                var body = await ReadBoundedBodyAsync(response.Content, MaximumHtmlBytes, token).ConfigureAwait(false);
                if (body is null)
                    return Unavailable("Swagger UI could not be verified: the response exceeded the bounded page check.");
                var html = HtmlComments.Replace(body.Value.Text, "");
                var scripts = Scripts.Matches(html);
                var initialized = scripts.Any(script => InitializeSwagger.IsMatch(script.Groups["script"].Value));
                var hasUiElements = SwaggerContainer.IsMatch(html) ||
                    scripts.Any(script => ScriptSource.Match(script.Groups["attributes"].Value) is { Success: true } source &&
                        source.Groups["source"].Value.Contains("swagger-ui-bundle", StringComparison.OrdinalIgnoreCase));
                if (!initialized && hasUiElements)
                {
                    // Standard Swagger UI and Swashbuckle pages initialize from a separate
                    // script. Read only a known initializer actually referenced by this page.
                    var initializer = scripts.Select(script => ScriptSource.Match(script.Groups["attributes"].Value))
                        .Where(source => source.Success)
                        .Select(source => Uri.TryCreate(current, WebUtility.HtmlDecode(source.Groups["source"].Value), out var scriptUri) ? scriptUri : null)
                        .OrderBy(scriptUri => scriptUri is not null && Path.GetFileName(scriptUri.AbsolutePath).Equals("swagger-initializer.js", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .FirstOrDefault(scriptUri => scriptUri is not null && IsKnownInitializer(configuredUi, scriptUri));
                    if (initializer is not null)
                    {
                        using var scriptRequest = new HttpRequestMessage(HttpMethod.Get, initializer);
                        using var scriptResponse = await Http.SendAsync(scriptRequest, HttpCompletionOption.ResponseHeadersRead, token)
                            .ConfigureAwait(false);
                        var scriptType = scriptResponse.Content.Headers.ContentType?.MediaType;
                        if (scriptResponse.StatusCode == HttpStatusCode.OK &&
                            (scriptType is null || scriptType.Contains("javascript", StringComparison.OrdinalIgnoreCase)))
                        {
                            var scriptBody = await ReadBoundedBodyAsync(scriptResponse.Content, MaximumHtmlBytes - body.Value.Bytes, token)
                                .ConfigureAwait(false);
                            initialized = scriptBody is not null && InitializeSwagger.IsMatch(scriptBody.Value.Text);
                        }
                    }
                }
                if (!initialized || !hasUiElements)
                    return Unavailable($"Swagger UI was not detected at {originalUrl}. Swagger may not be installed or enabled for the current API environment; check the configured Swagger path.");
                return new(SwaggerUiAvailability.Ready, $"Swagger UI detected. Open {originalUrl} in your default browser.", originalUrl);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("Swagger UI could not be verified within two seconds. Wait for the API to finish starting or check its configured Swagger path.");
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return Unavailable("Swagger UI could not be verified because the API's HTTPS certificate could not be validated. Check its trusted local development certificate.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or RegexMatchTimeoutException)
        {
            return Unavailable("Swagger UI could not be verified from the API response. Check the configured Swagger path and API output.");
        }
        return Unavailable("Swagger UI could not be verified at the configured address.");

        SwaggerUiStatus Unavailable(string message) => new(SwaggerUiAvailability.Unavailable, message, originalUrl);
    }

    private static async Task<(string Text, int Bytes)?> ReadBoundedBodyAsync(HttpContent content, int maximumBytes,
        CancellationToken token)
    {
        if (maximumBytes <= 0 || content.Headers.ContentLength > maximumBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)bytes.Length)), token)
                .ConfigureAwait(false);
            if (count == 0) break;
            bytes.Write(buffer, 0, count);
            if (bytes.Length > maximumBytes) return null;
        }
        // Standard Swagger page/script templates use UTF-8; identifying markup is ASCII.
        return (Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length), (int)bytes.Length);
    }

    private static bool IsKnownInitializer(Uri configured, Uri scriptUri) => IsSameOrigin(configured, scriptUri) &&
        Path.GetFileName(scriptUri.AbsolutePath) is var name &&
        (name.Equals("index.js", StringComparison.OrdinalIgnoreCase) || name.Equals("swagger-initializer.js", StringComparison.OrdinalIgnoreCase));

    private static bool IsSameOrigin(Uri configured, Uri target) => target.IsLoopback &&
        string.IsNullOrEmpty(target.UserInfo) && configured.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase) &&
        configured.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) && configured.Port == target.Port;

    private static bool IsCanonicalUiRedirect(Uri configured, Uri target)
    {
        if (!IsSameOrigin(configured, target)) return false;
        var path = configured.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/index.htm", StringComparison.OrdinalIgnoreCase))
            path = path[..path.LastIndexOf('/')];
        return target.AbsolutePath.Equals(configured.AbsolutePath, StringComparison.Ordinal) ||
            target.AbsolutePath.Equals(path, StringComparison.Ordinal) ||
            target.AbsolutePath.Equals(path + "/", StringComparison.Ordinal) ||
            target.AbsolutePath.Equals(path + "/index.html", StringComparison.Ordinal) ||
            target.AbsolutePath.Equals(path + "/index.htm", StringComparison.Ordinal);
    }
}
