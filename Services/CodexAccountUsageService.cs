using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using FullStackLauncher.Models;
using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher.Services;

/// <summary>Reads public account limits through a separate owned App Server child; never starts a turn.</summary>
public sealed class CodexAccountUsageService : IAsyncDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);
    private const string UnavailableMessage = "Codex allowance could not be read. Check Codex sign-in and connectivity.";
    private const string IncompatibleMessage = "Codex returned an unsupported allowance response. Update Codex and try again.";
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CodexAccountUsageSnapshot _snapshot = new();
    private DateTimeOffset _nextReadAt;
    private int _disposed;

    public async Task<CodexAccountUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _readLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow < _nextReadAt)
                return _snapshot;

            // Process discovery and startup also stay off the WPF dispatcher.
            _snapshot = await Task.Run(() => ReadCoreAsync(_snapshot, linked.Token), linked.Token).ConfigureAwait(false);
            _nextReadAt = _snapshot.LastAttemptedAt.Add(RefreshInterval);
            return _snapshot;
        }
        finally { _readLock.Release(); }
    }

    private static async Task<CodexAccountUsageSnapshot> ReadCoreAsync(
        CodexAccountUsageSnapshot previous, CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            await using var connection = await CodexAppServerConnection.StartAsync(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), timeout.Token).ConfigureAwait(false);
            var result = await connection.RequestAsync("account/rateLimits/read", new { }, timeout.Token).ConfigureAwait(false);
            return Parse(result, attemptedAt);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(previous, attemptedAt, "Codex allowance read timed out. Check Codex connectivity.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return Failure(previous, attemptedAt, IncompatibleMessage);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception
            or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Never retain raw backend, configuration, account, or credential diagnostics.
            return Failure(previous, attemptedAt, UnavailableMessage);
        }
    }

    private static CodexAccountUsageSnapshot Failure(
        CodexAccountUsageSnapshot previous, DateTimeOffset attemptedAt, string message) => previous with
    {
        LastAttemptedAt = attemptedAt,
        IsStale = previous.LastUpdatedAt is not null,
        StatusMessage = message
    };

    private static CodexAccountUsageSnapshot Parse(JsonElement result, DateTimeOffset attemptedAt)
    {
        if (result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(IncompatibleMessage);

        var bucket = SelectCodexBucket(result);
        if (bucket is null)
            return new CodexAccountUsageSnapshot
            {
                LastUpdatedAt = attemptedAt,
                LastAttemptedAt = attemptedAt,
                StatusMessage = "Codex allowance was not supplied for this account."
            };

        var value = bucket.Value;
        decimal? balance = null;
        bool? unlimited = null;
        bool? hasCredits = null;
        if (value.TryGetProperty("credits", out var credits) && credits.ValueKind != JsonValueKind.Null)
        {
            if (credits.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(IncompatibleMessage);
            unlimited = ReadOptionalBoolean(credits, "unlimited");
            hasCredits = ReadOptionalBoolean(credits, "hasCredits");
            if (credits.TryGetProperty("balance", out var creditBalance) && creditBalance.ValueKind != JsonValueKind.Null)
            {
                if (creditBalance.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException(IncompatibleMessage);
                var text = creditBalance.GetString();
                if (text is { Length: > 0 and <= 128 }
                    && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedBalance)
                    && parsedBalance >= 0
                    && (parsedBalance != 0 || !HasNonZeroSignificand(text)))
                    balance = parsedBalance;
            }
        }

        var snapshot = new CodexAccountUsageSnapshot
        {
            Primary = ReadWindow(value, "primary"),
            Secondary = ReadWindow(value, "secondary"),
            CreditsBalance = balance,
            UnlimitedCredits = unlimited,
            HasCredits = hasCredits,
            LastUpdatedAt = attemptedAt,
            LastAttemptedAt = attemptedAt
        };
        return snapshot.HasData ? snapshot : snapshot with
        {
            StatusMessage = "Codex allowance was not supplied for this account."
        };
    }

    private static JsonElement? SelectCodexBucket(JsonElement result)
    {
        var hasOtherBuckets = false;
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind != JsonValueKind.Null)
        {
            if (buckets.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(IncompatibleMessage);
            if (buckets.TryGetProperty("codex", out var codex) && codex.ValueKind != JsonValueKind.Null)
            {
                ValidateBucket(codex);
                return codex;
            }
            hasOtherBuckets = buckets.EnumerateObject().Any();
        }

        if (!result.TryGetProperty("rateLimits", out var legacy) || legacy.ValueKind == JsonValueKind.Null)
            return null;
        if (legacy.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(IncompatibleMessage);
        var limitId = ReadOptionalString(legacy, "limitId");
        // Historical payloads omit the ID. Do not mistake another named bucket for Codex.
        if (limitId is not null && !string.Equals(limitId, "codex", StringComparison.Ordinal))
            return null;
        if (hasOtherBuckets && limitId is null)
            return null;
        return legacy;
    }

    private static void ValidateBucket(JsonElement bucket)
    {
        if (bucket.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(IncompatibleMessage);
        var limitId = ReadOptionalString(bucket, "limitId");
        if (limitId is not null && !string.Equals(limitId, "codex", StringComparison.Ordinal))
            throw new InvalidDataException(IncompatibleMessage);
    }

    private static CodexAccountUsageWindow? ReadWindow(JsonElement bucket, string name)
    {
        if (!bucket.TryGetProperty(name, out var window) || window.ValueKind == JsonValueKind.Null)
            return null;
        if (window.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(IncompatibleMessage);
        if (!window.TryGetProperty("usedPercent", out var used) || used.ValueKind == JsonValueKind.Null)
            return null;
        if (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var usedPercent) || !double.IsFinite(usedPercent))
            throw new InvalidDataException(IncompatibleMessage);

        int? duration = null;
        if (window.TryGetProperty("windowDurationMins", out var minutes) && minutes.ValueKind != JsonValueKind.Null)
        {
            if (minutes.ValueKind != JsonValueKind.Number || !minutes.TryGetInt32(out var parsedMinutes) || parsedMinutes <= 0)
                throw new InvalidDataException(IncompatibleMessage);
            duration = parsedMinutes;
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind != JsonValueKind.Null)
        {
            if (reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out var unixSeconds))
                throw new InvalidDataException(IncompatibleMessage);
            try { resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
            catch (ArgumentOutOfRangeException) { throw new InvalidDataException(IncompatibleMessage); }
        }
        return new CodexAccountUsageWindow(Math.Clamp(100d - usedPercent, 0d, 100d), duration, resetsAt);
    }

    private static bool? ReadOptionalBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(IncompatibleMessage)
        };
    }

    private static bool HasNonZeroSignificand(string text)
    {
        // Decimal parsing can underflow a tiny positive amount to zero; leave that amount unknown.
        foreach (var character in text)
        {
            if (character is 'e' or 'E') break;
            if (character is >= '1' and <= '9') return true;
        }
        return false;
    }

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(IncompatibleMessage);
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        // Cancellation closes only this service's owned App Server child and uses its bounded cleanup.
        await _readLock.WaitAsync().ConfigureAwait(false);
        _readLock.Release();
        // Concurrent callers can still be leaving ReadAsync; keep their synchronization objects alive.
    }
}
