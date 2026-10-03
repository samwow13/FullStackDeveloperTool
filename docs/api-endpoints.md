# API endpoint inventory

The **API endpoints…** button is in each API/.NET service card's Services toolbar, outside the collapsed details. It uses the launcher's existing API-service marker; frontend and console cards do not get the button. Opening it automatically scans the configured service working folder. The API can remain stopped.

The resizable window shows discovered operation totals, counts for GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS, TRACE, CONNECT and other declared verbs, filterable route templates, controller breakdowns and declaration filenames/line numbers. Select an operation to inspect all its origins. **Copy inventory** includes the full result and its limitations, regardless of the current filter.

## Counting semantics

After a scan or JSON import completes, the service card shows **N API endpoints** beside its total code lines and average lines per file, including when details are collapsed. This is the full inventory total across all HTTP methods, independent of window filters. Partial inventories show **partial** and an amber label. The tooltip explains the source, count time and counting semantics. The total remains after closing the inventory window for the current launcher session. Starting a new scan clears it; canceled or failed scans do not show an old total as current. Endpoint totals are not saved in settings.

An endpoint operation is a distinct HTTP-method + route-template pair. A controller action with two routes and two verbs can yield four operations. Repeated pairs count once, with their declaration origins preserved. This is a metadata inventory; it does not establish that overlapping routes can be served unambiguously. Route parameter names and constraints remain part of the template, and literal route casing is preserved. **ALL** represents a route without an HTTP-method constraint as one metadata entry; it is not expanded into invented per-verb counts. GET does not imply a separately counted HEAD.

The source summary separately counts eligible public instance action methods declared in discovered controllers, including unresolved/conventional actions. Inherited methods are excluded. Controller breakdown rows show methods with resolved operations; they can overlap across controllers, so their counts need not sum to the deduplicated total.

## Sources and limitations

**Scan source** inspects C# declarations with Roslyn without evaluating MSBuild, compiling/loading the API assembly, starting its host, or invoking methods. It supports ordinary ASP.NET Core controller selection, standard route/HTTP-method attributes, AcceptVerbs, resolvable constant strings, route combinations, absolute action routes and controller/action/area tokens. Multiple selectors follow standard MVC attribute-routing rules.

Source discovery requires exactly one top-level `.csproj` in the service working folder. Source results are explicitly **partial**: runtime registration, generated code, referenced assemblies, inherited actions, custom conventions/attributes, conditional build symbols and evaluated MSBuild source membership can change the real inventory. Unsupported or unresolved declarations produce limitations rather than guessed operations. Projects disabling default compile membership require OpenAPI import. Output, test, hidden and dependency directories and filesystem links are skipped; bounded scans stop at 4,000 files, 8,000 directories, 4 MiB per source file, 64 MiB combined source or 50,000 operations.

**OpenAPI JSON…** reads a chosen local OpenAPI 3.0/3.1 or Swagger 2.0 JSON snapshot (maximum 16 MiB). It counts documented HTTP operations under top-level `paths`, applying Swagger `basePath`; server alternatives do not multiply operations. OpenAPI cannot establish controller/action method counts. Its breakdown uses the first tag of each operation. Local path-item references are supported; external, cyclic, ambiguous and unsupported references are flagged and omitted. Metadata keys, callbacks and webhooks are outside the top-level path count. JSON format/version errors are shown explicitly; YAML and OpenAPI 3.2 are unsupported.

No source discovery or import makes an HTTP request or calls business endpoints. Snapshot completeness, freshness, runtime environment, authorization, route equivalence and custom application behavior remain unverified. **Refresh** rereads the current source or selected JSON file. **Scan source** returns from JSON to source. Cancel and close cancel pending work; failed/canceled refreshes retire old counts. Results and file selections stay in the window's session and never enter settings.

## Focused verification

The user-authorized routing/discovery checks run with:

```powershell
dotnet run --project Tests/ApiEndpointChecks/ApiEndpointChecks.csproj -c Release
dotnet build FullStackLauncher.csproj -c Release
```

These checks use in-memory C# and local temporary JSON/source files. They do not start services, contact APIs, read launcher settings or mutate business data. Actual desktop WPF interaction is separate from compiler and discovery checks.
