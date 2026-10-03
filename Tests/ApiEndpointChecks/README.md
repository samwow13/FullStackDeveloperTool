# API endpoint checks

Run from the launcher source folder:

```powershell
dotnet restore Tests/ApiEndpointChecks/ApiEndpointChecks.csproj --ignore-failed-sources --property:NuGetAudit=false
dotnet run --project Tests/ApiEndpointChecks/ApiEndpointChecks.csproj -c Release --no-restore
```

The 40 focused console checks link the production inventory/discovery files. They check attributed controller route and verb semantics, action eligibility including routed ordinary Dispose versus direct IDisposable implementations and partial methods, area metadata, distinct operation pairs/source origins including same-line overloads and namespace identity, case-sensitive template identity, attribute aliases/custom attributes, unsupported source declarations, OpenAPI/Swagger metadata/versions/references/duplicate keys, refresh/exclusions, size limits, cancellation, empty results and file errors. Fixtures are source strings and isolated temporary files only. They never launch an API, send HTTP, invoke source methods, open WPF, access launcher settings or play audio.

These are metadata checks rather than runtime ASP.NET integration checks. Source inventories always disclose their static coverage boundary and remain partial. Runtime route conventions, custom attributes, inherited actions, conditional compilation and Minimal API code require coverage warnings or an exported OpenAPI document; runtime endpoint availability is not established by this harness.
