using Microsoft.AspNetCore.Diagnostics;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.Engine;
using SPOMover.Ui;
using SPOMover.Ui.Services;
using SPOMover.State;

namespace SPOMover.Host;

/// <param name="Update">INV-5: inventory with changes only since the last finished inventory of the profile.</param>
/// <param name="Plan">Apply: the plan to move; null — the newest plan of the profile.</param>
public sealed record CreateRunRequest(string Mode, string? Profile, bool Start = true, bool Update = false, long? Plan = null);

public sealed record IgnoreRequest(string Comment);

/// <summary>retry: error classes and/or file ids; empty — every failed and deferred file.</summary>
public sealed record RetryRequest(string[]? Classes = null, long[]? Files = null, bool Start = true);

/// <summary>MAP-7: where a path of the source goes.</summary>
public sealed record MapTestResult(string Path, int? Row, string? Source, string? Library, string? NewPath, bool Stays, string Text);

public sealed record IssueDto(string Severity, string Message, int? Row)
{
    public static List<IssueDto> From(IEnumerable<ValidationIssue> issues) =>
        issues.Select(i => new IssueDto(i.Severity == IssueSeverity.Error ? "error" : "warning", i.Message, i.Row)).ToList();
}

public sealed record FileDetails(FileRow File, IReadOnlyList<AttemptRow> Attempts);

internal static class Api
{
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["profiles"] = SettingKinds.Profile,
        ["connections"] = SettingKinds.Connection,
    };

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/health", () => Results.Ok(new { status = "ok", version = typeof(Api).Assembly.GetName().Version?.ToString() }));

        // ---- settings (CFG-1, GUI-5)
        api.MapGet("/{kind}", (string kind, RunService runs) =>
            Results.Ok(runs.Store.ListSettings(Kind(kind)).Select(s => new { s.Name, s.UpdatedAt })));
        api.MapGet("/{kind}/{name}", (string kind, string name, bool? download, RunService runs) =>
            runs.Store.GetSetting(Kind(kind), name) is { } json
                ? download == true
                    ? Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"{name}.{Kind(kind)}.json")
                    : Results.Text(json, "application/json")
                : Results.NotFound());
        api.MapPut("/{kind}/{name}", async (string kind, string name, HttpRequest request, SettingsService settings) =>
        {
            using var reader = new StreamReader(request.Body);
            var issues = settings.SaveJson(Kind(kind), name, await reader.ReadToEndAsync());
            return Results.Ok(new { name, issues = IssueDto.From(issues) });
        });
        api.MapDelete("/{kind}/{name}", (string kind, string name, SettingsService settings) =>
            settings.Delete(Kind(kind), name) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/profiles/{name}/validate", (string name, RunService runs) =>
        {
            var v = runs.ValidateProfile(runs.GetProfile(name));
            return Results.Ok(new { valid = v.IsValid, issues = IssueDto.From(v.Issues) });
        });
        // ---- mapping (MAP-5…MAP-7)
        api.MapGet("/profiles/{name}/mapping/test", (string name, string[] path, RunService runs) =>
        {
            var set = runs.GetProfile(name).BuildMapping().Set ?? Core.Mapping.MappingSet.Empty;
            return Results.Ok(path.Select(p => set.Resolve(p) is { } m
                ? new MapTestResult(p, m.Rule.Row, m.Rule.Source, m.Library, m.NewPath, m.Stays,
                    m.Stays ? L.T($"stays in the source (row {m.Rule.Row})") : L.T($"{m.Library}:/{m.NewPath} (row {m.Rule.Row})"))
                : new MapTestResult(p, null, null, null, null, true, L.T("no rule matches — stays in the source"))));
        });
        // Before a move: the target libraries against the source (columns, content types, settings). Reads only.
        api.MapGet("/profiles/{name}/target-check", async (string name, RunService runs, SpoBrowser browser, CancellationToken ct) =>
        {
            var report = await browser.CheckTargetsAsync(runs.GetProfile(name), ct);
            return Results.Ok(new { ok = report.Ok, warnings = report.Warnings, text = TargetCheck.Text(report), report.Findings });
        });
        api.MapPost("/profiles/{name}/mapping/check", (string name, RunService runs) =>
        {
            var report = MappingCheck.Run(runs.Store, runs.GetProfile(name));
            return Results.Ok(new { report, summary = MappingCheck.Summary(report), issues = IssueDto.From(report.Issues) });
        });
        api.MapGet("/profiles/{name}/mapping/reports/{report}", (string name, string report, string? format, RunService runs) =>
        {
            if (!MappingCheck.Reports.Contains(report))
                return Results.NotFound();
            var profile = runs.GetProfile(name);
            var result = MappingCheck.Run(runs.Store, profile);
            var ms = new MemoryStream();
            Exports.WriteMappingReport(runs.Store, profile, result, report, format ?? "csv", ms);
            ms.Position = 0;
            return Results.File(ms, format == "xlsx" ? Exports.XlsxType : "text/csv; charset=utf-8", Exports.MappingFileName(name, report, format ?? "csv"));
        });
        api.MapGet("/profiles/{name}/mapping/csv", (string name, RunService runs) =>
            Results.File(Core.Mapping.MappingSet.ToCsv(runs.GetProfile(name).Mapping), "text/csv; charset=utf-8", $"{name}.mapping.csv"));
        // The body is the file itself; append=false replaces the mapping.
        api.MapPost("/profiles/{name}/mapping/import", async (string name, string? file, bool? append, HttpRequest request, SettingsService settings, RunService runs) =>
        {
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            ms.Position = 0;
            var imported = Core.Mapping.MappingImport.Parse(file ?? "mapping.csv", ms);
            var profile = runs.GetProfile(name);
            profile.Mapping = append == true ? [.. profile.Mapping, .. imported.Entries] : imported.Entries;
            var issues = settings.Save(name, profile);
            return Results.Ok(new { rows = imported.Entries.Count, total = profile.Mapping.Count, warnings = imported.Warnings, issues = IssueDto.From(issues) });
        });
        // ---- plan (PLN-1…PLN-5)
        api.MapPost("/profiles/{name}/plan", (string name, RunService runs, EngineOptions engine) =>
        {
            var report = Planner.Build(runs.Store, runs.GetProfile(name), engine.Time.GetUtcNow());
            return Results.Ok(new { plan = report.Plan.Id, report, summary = Planner.Summary(report), issues = IssueDto.From(report.Issues) });
        });
        api.MapGet("/profiles/{name}/plans", (string name, RunService runs) => Results.Ok(runs.Store.ListPlans(name)));
        api.MapGet("/plans/{id:long}", (long id, RunService runs) =>
        {
            if (runs.Store.GetPlan(id) is null)
                return Results.NotFound();
            var report = Planner.Report(runs.Store, id);
            return Results.Ok(new { plan = id, report, summary = Planner.Summary(report) });
        });
        api.MapGet("/plans/{id:long}/reports/{report}", (long id, string report, string? format, RunService runs) =>
        {
            var plan = runs.Store.GetPlan(id);
            if (plan is null || !Planner.Reports.Contains(report))
                return Results.NotFound();
            var ms = new MemoryStream();
            Exports.WritePlanReport(runs.Store, id, report, format ?? "csv", ms);
            ms.Position = 0;
            return Results.File(ms, format == "xlsx" ? Exports.XlsxType : "text/csv; charset=utf-8", Exports.PlanFileName(plan.ProfileName ?? "plan", id, report, format ?? "csv"));
        });
        api.MapPost("/connections/{name}/test", async (string name, string? profile, RunService runs, CancellationToken ct) =>
            Results.Ok(await runs.TestConnectionAsync(name, profile, ct)));

        // ---- runs (FEATURES 7, STA-4, STA-5)
        api.MapGet("/runs", (RunService runs, int? take) => Results.Ok(runs.Store.ListRuns(take ?? 50).Select(RunInfo.From)));
        api.MapPost("/runs", (CreateRunRequest request, RunService runs) =>
        {
            long id = request.Mode == RunModes.Apply
                ? runs.CreateApplyRun(request.Profile ?? throw new ArgumentException(L.T("The profile is not specified.")), request.Plan)
                : runs.CreateRun(request.Mode, request.Profile, update: request.Update);
            if (request.Start)
                runs.Start(id);
            return Results.Created($"/api/runs/{id}", new { id });
        });
        api.MapGet("/runs/{id:long}", (long id, StatusService status) => status.Get(id, fresh: true) is { } s ? Results.Ok(s) : Results.NotFound());
        api.MapPost("/runs/{id:long}/start", (long id, RunService runs) => Ok(() => runs.Start(id)));
        api.MapPost("/runs/{id:long}/pause", (long id, RunService runs) => Ok(() => runs.Pause(id)));
        api.MapPost("/runs/{id:long}/resume", (long id, RunService runs) => Ok(() => runs.Resume(id)));
        api.MapPost("/runs/{id:long}/stop", (long id, RunService runs) => Ok(() => runs.Stop(id)));
        api.MapDelete("/runs/{id:long}", (long id, RunService runs) => Ok(() => runs.DeleteRun(id)));
        api.MapGet("/runs/{id:long}/files", (long id, RunService runs, string? status, string? errorClass, string? ext, string? search, int? target, int? skip, int? take) =>
        {
            var filter = Filter(status, errorClass, ext, search, target);
            return Results.Ok(new
            {
                total = runs.Store.CountFiles(id, filter),
                items = runs.Store.QueryFiles(id, filter, skip ?? 0, Math.Clamp(take ?? 100, 1, 1000)),
            });
        });
        api.MapGet("/runs/{id:long}/metrics", (long id, RunService runs) =>
            Results.Ok(runs.Store.GetMetrics(id).Select(m => new { at = m.At, data = System.Text.Json.JsonDocument.Parse(m.Json).RootElement })));
        // ---- after apply: verify, retry, rules for SPOReplaceLink (FEATURES 8, MAP-8)
        api.MapPost("/runs/{id:long}/verify", (long id, RunService runs, EngineOptions engine) =>
        {
            var report = ApplyResults.Verify(runs.Store, id, engine.Time.GetUtcNow());
            return Results.Ok(new { report.Verified, report.Failed, report.NotMoved, newInSource = report.NewInSource.Count, report.Ok, summary = ApplyResults.Summary(report) });
        });
        api.MapPost("/runs/{id:long}/retry", (long id, RetryRequest request, RunService runs) =>
        {
            int n = runs.Retry(id, request.Classes, request.Files);
            if (request.Start)
                runs.Start(id);
            return Results.Ok(new { files = n });
        });
        // EXE-7: what a cleanup of an apply run would look at, then the cleanup run itself (started at once).
        api.MapGet("/runs/{id:long}/cleanup", (long id, RunService runs) => Results.Ok(runs.PreviewCleanup(id)));
        api.MapPost("/runs/{id:long}/cleanup", (long id, RunService runs) =>
        {
            long cleanup = runs.CreateCleanupRun(id);
            runs.Start(cleanup);
            return Results.Created($"/api/runs/{cleanup}", new { id = cleanup });
        });
        api.MapGet("/runs/{id:long}/apply-reports/{name}", (long id, string name, string? format, RunService runs, EngineOptions engine) =>
        {
            if (!ApplyResults.Reports.Contains(name))
                return Results.NotFound();
            var report = name == "verify" ? ApplyResults.Verify(runs.Store, id, engine.Time.GetUtcNow()) : null;
            var ms = new MemoryStream();
            ApplyResults.Write(runs.Store, id, name, format ?? "csv", ms, report);
            ms.Position = 0;
            if (ApplyResults.IsText(name))
                return Results.File(ms, "text/plain; charset=utf-8", $"run-{id}-{name}.txt");
            return Results.File(ms, format == "xlsx" ? Exports.XlsxType : "text/csv; charset=utf-8", $"run-{id}-{name}.{(format == "xlsx" ? "xlsx" : "csv")}");
        });
        api.MapGet("/runs/{id:long}/reports/{name}", (long id, string name, string? format, RunService runs) =>
        {
            if (!ReportExporter.Names.Contains(name) || runs.Store.GetRun(id) is null)
                return Results.NotFound();
            var ms = new MemoryStream();
            Exports.WriteReport(runs.Store, id, name, format ?? "csv", ms);
            ms.Position = 0;
            string type = format == "xlsx" ? Exports.XlsxType : ReportExporter.IsText(name) ? "text/plain; charset=utf-8" : "text/csv; charset=utf-8";
            return Results.File(ms, type, Exports.ReportFileName(id, name, format ?? "csv"));
        });
        // GUI-4: the files table with the same filter as on screen.
        api.MapGet("/runs/{id:long}/files/export", (long id, RunService runs, string? format, string? status, string? errorClass, string? ext, string? search, int? target) =>
        {
            var ms = new MemoryStream();
            Exports.WriteFiles(runs.Store, id, Filter(status, errorClass, ext, search, target), format ?? "csv", ms);
            ms.Position = 0;
            return format == "xlsx"
                ? Results.File(ms, Exports.XlsxType, $"run-{id}-files.xlsx")
                : Results.File(ms, "text/csv; charset=utf-8", $"run-{id}-files.csv");
        });
        // ---- files (GUI-4, RTY-6)
        api.MapGet("/files/{id:long}", (long id, RunService runs) =>
            runs.Store.GetFile(id) is { } f
                ? Results.Ok(new FileDetails(f, runs.Store.GetAttempts(id)))
                : Results.NotFound());
        api.MapPost("/files/{id:long}/ignore", (long id, IgnoreRequest request, RunService runs) =>
            string.IsNullOrWhiteSpace(request.Comment)
                ? Results.BadRequest(new { error = L.T("A comment is required: why the file is ignored.") })
                : runs.Store.Ignore(id, request.Comment, DateTimeOffset.UtcNow) ? Results.NoContent() : Results.NotFound());
    }

    public static FileFilter Filter(string? status, string? errorClass, string? ext, string? search, int? target) => new()
    {
        Statuses = Split(status),
        ErrorClasses = Split(errorClass),
        Extensions = Split(ext),
        Search = search,
        TargetIndex = target,
    };

    private static IResult Ok(Action action)
    {
        action();
        return Results.NoContent();
    }

    private static string Kind(string kind) =>
        Kinds.TryGetValue(kind, out var k) ? k : throw new KeyNotFoundException(L.T($"Unknown section \"{kind}\"."));

    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

internal static class ApiErrors
{
    public static async Task Handle(HttpContext context)
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        (int status, object body) = error switch
        {
            RunValidationException v => (400, (object)new { error = v.Message, issues = IssueDto.From(v.Issues) }),
            ProfileException or ArgumentException or BadHttpRequestException => (400, new { error = error.Message }),
            KeyNotFoundException => (404, new { error = error.Message }),
            InvalidOperationException => (409, new { error = error.Message }),
            _ => (500, new { error = error?.Message ?? L.T("Internal error") }),
        };
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(body);
    }
}
