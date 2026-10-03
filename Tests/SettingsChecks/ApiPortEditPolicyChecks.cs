using FullStackLauncher.Models;
using FullStackLauncher.Services;

internal static class ApiPortEditPolicyChecks
{
    public static void LinkedFrontend()
    {
        var api = Api();
        var linked = Frontend("linked");
        linked.ApiTargetServiceId = api.Id;
        var unrelated = Frontend("unrelated");
        var services = new[] { api, linked, unrelated };
        var states = new Dictionary<string, (bool Stopped, bool Verified)>
        {
            [api.Id] = (true, true), [linked.Id] = (false, true),
            [unrelated.Id] = (false, true)
        };
        bool IsStopped(ServiceProfile service) =>
            states[service.Id] is { Stopped: true, Verified: true };

        Require(ReferenceEquals(linked, ApiPortEditPolicy.FirstBlockingService(api, services, IsStopped)),
            "Running linked Angular frontend must block API port edit.");
        states[linked.Id] = (true, false);
        Require(ReferenceEquals(linked, ApiPortEditPolicy.FirstBlockingService(api, services, IsStopped)),
            "Unverified linked Angular frontend must block API port edit.");
        states[linked.Id] = (true, true);
        Require(ApiPortEditPolicy.FirstBlockingService(api, services, IsStopped) is null,
            "Verified stopped API and linked frontend must allow edit; unrelated frontend does not block explicit link.");
        states[api.Id] = (false, true);
        Require(ReferenceEquals(api, ApiPortEditPolicy.FirstBlockingService(api, services, IsStopped)),
            "Running API must block its own port edit.");
        states[api.Id] = (true, false);
        Require(ReferenceEquals(api, ApiPortEditPolicy.FirstBlockingService(api, services, IsStopped)),
            "Unverified API must block its own port edit.");
    }

    public static void LegacyFrontend()
    {
        var api = Api();
        var legacy = Frontend("legacy");
        var manual = Frontend("manual");
        manual.DisableLegacyApiPortSync = true;
        var services = new[] { api, legacy, manual };
        var stopped = new HashSet<string> { api.Id };
        bool IsStopped(ServiceProfile service) => stopped.Contains(service.Id);

        Require(ReferenceEquals(legacy, ApiPortEditPolicy.FirstBlockingService(api, services, IsStopped)),
            "Running legacy Angular frontend must block possible proxy rewrite.");
        stopped.Add(legacy.Id);
        Require(ApiPortEditPolicy.FirstBlockingService(api, services, IsStopped) is null,
            "Verified stopped API and legacy frontend must allow edit; manual frontend does not block.");
        stopped.Remove(legacy.Id);
        Require(ReferenceEquals(legacy, ApiPortEditPolicy.FirstBlockingService(api, services, IsStopped)),
            "Unverified legacy Angular frontend must block possible proxy rewrite.");
    }

    private static ServiceProfile Api() => new()
    {
        Id = "api", Name = "CRM API", Kind = ".NET", Url = "http://localhost:5170"
    };

    private static ServiceProfile Frontend(string id) => new()
    {
        Id = id, Name = id, Kind = "Angular", Url = "http://localhost:4200"
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
