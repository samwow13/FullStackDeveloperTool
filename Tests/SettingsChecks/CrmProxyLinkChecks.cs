using System.Text.Json;
using System.Text.Json.Nodes;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

internal static class CrmProxyLinkChecks
{
    public static void LinkAndUpdate()
    {
        using var fixture = new CrmProxyFixture();
        var angularBefore = File.ReadAllBytes(fixture.AngularPath);
        var sourceProxyBefore = File.ReadAllBytes(fixture.SourceProxyPath);
        var sourceAngularBefore = File.ReadAllBytes(fixture.SourceAngularPath);
        var originalTarget = fixture.RouteTarget("/api/**");
        var linkedPort = new Uri(originalTarget).Port == 5317 ? 5318 : 5317;
        var (linked, store) = fixture.SetUpLink(linkedPort);

        Require(fixture.RouteTarget("/api/**") == $"http://localhost:{linkedPort}",
            "Link setup must use the API service's configured port.");
        Require(store.Load().Projects.Single().Services.Single(service => service.Id == "frontend").ApiTargetServiceId == "api",
            "Link must persist in launcher settings.");

        // An unrelated development route may legitimately share the API's old port.
        // A linked API edit owns only the selected API route.
        fixture.AddRoute("/other/**", $"http://localhost:{linkedPort}");
        var updatedPort = linkedPort + 1;
        var updated = Clone(linked);
        updated.Services.Single(service => service.Id == "api").Url = $"http://localhost:{updatedPort}";
        var change = AngularDevProxyConfiguration.Prepare(linked, updated, store);
        Require(change.UpdatedFileCount == 1, "API port edit must stage the CRM development proxy.");
        change.SaveWithSettings(() => store.Save(new LauncherSettings { Projects = [updated] }));

        Require(fixture.RouteTarget("/api/**") == $"http://localhost:{updatedPort}",
            "Linked API route must use the new port.");
        Require(fixture.RouteTarget("/other/**") == $"http://localhost:{linkedPort}",
            "Unrelated route sharing the previous API port must stay unchanged.");
        Require(store.Load().Projects.Single().Services.Single(service => service.Id == "api").Url ==
                $"http://localhost:{updatedPort}", "New API URL must persist in launcher settings.");
        Require(File.ReadAllBytes(fixture.AngularPath).SequenceEqual(angularBefore),
            "Angular build and production configuration must stay byte-for-byte unchanged.");
        Require(File.ReadAllBytes(fixture.SourceProxyPath).SequenceEqual(sourceProxyBefore) &&
                File.ReadAllBytes(fixture.SourceAngularPath).SequenceEqual(sourceAngularBefore),
            "Integration test must not write its checked-in CRM configuration fixtures.");
    }

    public static void ProxyDriftBlocksSave()
    {
        using var fixture = new CrmProxyFixture();
        var (linked, store) = fixture.SetUpLink(5317);
        fixture.SetRouteTarget("/api/**", "http://localhost:5499");
        var changedProxy = File.ReadAllBytes(fixture.ProxyPath);
        var savedSettings = File.ReadAllBytes(fixture.SettingsPath);
        var updated = Clone(linked);
        updated.Services.Single(service => service.Id == "api").Url = "http://localhost:5318";

        ExpectInvalidOperation(() => AngularDevProxyConfiguration.Prepare(linked, updated, store),
            "no local Angular proxy target matches");
        Require(File.ReadAllBytes(fixture.ProxyPath).SequenceEqual(changedProxy),
            "Proxy drift must not be overwritten.");
        Require(File.ReadAllBytes(fixture.SettingsPath).SequenceEqual(savedSettings),
            "Proxy drift must leave saved API port unchanged.");
    }

    public static void SettingsFailureRollsBackProxy()
    {
        using var fixture = new CrmProxyFixture();
        var (linked, store) = fixture.SetUpLink(5317);
        var beforeProxy = File.ReadAllBytes(fixture.ProxyPath);
        var updated = Clone(linked);
        updated.Services.Single(service => service.Id == "api").Url = "http://localhost:5318";
        var change = AngularDevProxyConfiguration.Prepare(linked, updated, store);

        ExpectInvalidOperation(() => change.SaveWithSettings(() =>
            throw new InvalidOperationException("synthetic settings failure")), "synthetic settings failure");
        Require(File.ReadAllBytes(fixture.ProxyPath).SequenceEqual(beforeProxy),
            "Failed settings save must restore previous proxy bytes.");
    }

    private static ProjectProfile Clone(ProjectProfile project) =>
        JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(project))!;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectInvalidOperation(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException exception)
        {
            Require(exception.Message.Contains(message, StringComparison.OrdinalIgnoreCase),
                $"Expected error containing '{message}', got '{exception.Message}'.");
            return;
        }
        throw new InvalidOperationException($"Expected InvalidOperationException containing '{message}'.");
    }

    private sealed class CrmProxyFixture : IDisposable
    {
        private static readonly string FixtureParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "FullStackLauncher.CrmProxyChecks"));
        private readonly string _name = Guid.NewGuid().ToString("N");

        public string Root { get; }
        public string SettingsPath => Path.Combine(Root, "settings.json");
        public string FrontendPath => Path.Combine(Root, "frontend");
        public string AngularPath => Path.Combine(FrontendPath, "angular.json");
        public string ProxyPath => Path.Combine(FrontendPath, "proxy.conf.json");
        public string SourceAngularPath { get; }
        public string SourceProxyPath { get; }

        public CrmProxyFixture()
        {
            var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CrmAngular");
            SourceAngularPath = Path.Combine(source, "angular.json");
            SourceProxyPath = Path.Combine(source, "proxy.conf.json");
            Require(File.Exists(SourceAngularPath) && File.Exists(SourceProxyPath),
                "CRM Angular configuration fixtures are missing from the test output.");
            Root = Path.Combine(FixtureParent, _name);
            Directory.CreateDirectory(FrontendPath);
            Directory.CreateDirectory(Path.Combine(Root, "api"));
            File.Copy(SourceAngularPath, AngularPath);
            File.Copy(SourceProxyPath, ProxyPath);
        }

        public (ProjectProfile Project, SettingsStore Store) SetUpLink(int apiPort)
        {
            var original = new ProjectProfile
            {
                Id = "crm", Name = "CRM", RootPath = Root,
                Services =
                [
                    new ServiceProfile { Id = "api", Name = "CRM API", Kind = ".NET",
                        WorkingDirectory = "api", StartCommand = "dotnet run",
                        Url = $"http://localhost:{apiPort}", UiPath = "/swagger" },
                    new ServiceProfile { Id = "frontend", Name = "CRM Angular", Kind = "Angular",
                        WorkingDirectory = "frontend", StartCommand = "npm start",
                        Url = "http://localhost:4200", UiPath = "/" }
                ]
            };
            var store = new SettingsStore(SettingsPath);
            store.Load();
            store.Save(new LauncherSettings { Projects = [original] });
            var linked = Clone(original);
            var frontend = linked.Services.Single(service => service.Id == "frontend");
            frontend.ApiTargetServiceId = "api";
            frontend.DisableLegacyApiPortSync = true;
            var change = AngularDevProxyConfiguration.Prepare(original, linked, store);
            Require(change.UpdatedFileCount == 1, "Initial link must stage CRM proxy target update.");
            change.SaveWithSettings(() => store.Save(new LauncherSettings { Projects = [linked] }));
            return (linked, store);
        }

        public string RouteTarget(string route) => JsonNode.Parse(File.ReadAllText(ProxyPath))!
            [route]!["target"]!.GetValue<string>();

        public void SetRouteTarget(string route, string target)
        {
            var proxy = JsonNode.Parse(File.ReadAllText(ProxyPath))!.AsObject();
            proxy[route]!["target"] = target;
            File.WriteAllText(ProxyPath, proxy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public void AddRoute(string route, string target)
        {
            var proxy = JsonNode.Parse(File.ReadAllText(ProxyPath))!.AsObject();
            proxy[route] = new JsonObject { ["target"] = target, ["changeOrigin"] = true };
            File.WriteAllText(ProxyPath, proxy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(FixtureParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || Path.GetFileName(resolved) != _name)
                throw new InvalidOperationException("Refusing to remove a CRM proxy fixture outside its test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }

    }
}
