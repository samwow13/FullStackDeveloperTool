using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

internal static class FrontendServiceSupport
{
    internal static bool IsFrontend(ServiceProfile profile) => !profile.IsConsole &&
        profile.ApiConfiguration is null && profile.Kind.Trim().ToLowerInvariant() is
            "angular" or "react" or "vue" or "vite" or "next" or "next.js" or "frontend";
}
