using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Identifies Angular services affected by a development API port edit.</summary>
public static class ApiPortEditPolicy
{
    public static ServiceProfile[] DependentAngularFrontends(ServiceProfile api,
        IReadOnlyList<ServiceProfile> services)
    {
        var linked = services.Where(frontend =>
            string.Equals(frontend.ApiTargetServiceId, api.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (linked.Length > 0 || !SettingsStore.IsLinkableApiService(api)) return linked;

        // Legacy profiles match proxy targets by port until an explicit link is saved.
        // A frontend in that mode may have its development proxy rewritten.
        return services.Where(frontend =>
            frontend.Kind.Equals("Angular", StringComparison.OrdinalIgnoreCase) &&
            frontend.ApiTargetServiceId is null && !frontend.DisableLegacyApiPortSync)
            .ToArray();
    }

    public static ServiceProfile? FirstBlockingService(ServiceProfile api,
        IReadOnlyList<ServiceProfile> services, Func<ServiceProfile, bool> isStopped)
    {
        if (!isStopped(api)) return api;
        return DependentAngularFrontends(api, services).FirstOrDefault(frontend => !isStopped(frontend));
    }
}
