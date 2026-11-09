using System.CommandLine;
using System.Text;
using SPOMover.Cli;
using SPOMover.Core.Localization;

Console.OutputEncoding = Encoding.UTF8;

// The language is chosen before the commands are built: their descriptions are translated at construction.
// --lang applies to this call only; the saved choice (language.txt) belongs to the app.
L.Load(SPOMover.Core.Hosting.ApiEndpoint.DataDirectory(PreScan("--data") ?? Environment.GetEnvironmentVariable("SPO_MOVER_DATA")));
if (PreScan("--lang") is { } language)
    L.Set(language);

string? PreScan(string name)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == name && i + 1 < args.Length)
            return args[i + 1];
        if (args[i].Length > name.Length + 1 && args[i].StartsWith(name, StringComparison.Ordinal) && args[i][name.Length] is '=' or ':')
            return args[i][(name.Length + 1)..];
    }
    return null;
}

// Ctrl+C while waiting asks the host for a graceful stop instead of killing the CLI at once.
using var interrupt = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (!interrupt.IsCancellationRequested)
    {
        e.Cancel = true;
        interrupt.Cancel();
    }
};

var root = new RootCommand(L.T("SPOMover — controlled splitting of a SharePoint Online library into several libraries"));

// ---------------------------------------------------------------- commands that work through the host

var hostOption = new Option<string?>("--host")
{
    Description = L.T("Host address (default SPO_MOVER_HOST, otherwise the address of this user's app from api.json, otherwise http://localhost:5090)"),
    Recursive = true,
};
root.Options.Add(hostOption);
var dataOption = new Option<string?>("--data")
{
    Description = L.T("Data folder of the app if it was started with --data (the CLI takes the API address and key from there; default SPO_MOVER_DATA or %LOCALAPPDATA%\\SPOMover)"),
    Recursive = true,
};
root.Options.Add(dataOption);
var langOption = new Option<string?>("--lang")
{
    Description = L.T("Language of the CLI output: en or uk (default SPO_MOVER_LANG, otherwise the language chosen in the app, otherwise the Windows language)"),
    Recursive = true,
};
langOption.AcceptOnlyFromAmong(L.English, L.Ukrainian);
root.Options.Add(langOption);

Command Cmd(string name, string description, Func<ParseResult, HostCommands, Task<int>> action, params Symbol[] symbols)
{
    var command = new Command(name, description);
    foreach (var symbol in symbols)
    {
        if (symbol is Argument argument)
            command.Arguments.Add(argument);
        else
            command.Options.Add((Option)symbol);
    }
    command.SetAction(async (parse, _) =>
    {
        var endpoint = SPOMover.Core.Hosting.ApiEndpoint.Read(SPOMover.Core.Hosting.ApiEndpoint.DataDirectory(
            parse.GetValue(dataOption) ?? Environment.GetEnvironmentVariable("SPO_MOVER_DATA")));
        string url = parse.GetValue(hostOption) ?? Environment.GetEnvironmentVariable("SPO_MOVER_HOST")
            ?? endpoint?.Url ?? SPOMover.Core.Hosting.ApiEndpoint.DefaultUrl;
        try
        {
            return await action(parse, new HostCommands(HostClient.Create(url, key: endpoint?.Key), Console.Out));
        }
        catch (HostException ex)
        {
            Console.Error.WriteLine(L.T($"ERROR: {ex.Message}"));
            foreach (string issue in ex.Issues)
                Console.Error.WriteLine("  " + issue);
            return ExitCodes.InvalidInput;
        }
    });
    return command;
}

var nameArgument = new Argument<string>("name") { Description = L.T("Name") };
var fileArgument = new Argument<string>("file") { Description = L.T("JSON file") };
var importName = new Option<string?>("--name") { Description = L.T("Name (default from the name field or the file name)") };
var outputFile = new Option<string?>("--output", "-o") { Description = L.T("Write to a file") };
var profileOption = new Option<string?>("--profile") { Description = L.T("Profile") };
foreach (var (kind, title) in new[] { ("profiles", L.T("Run profiles")), ("connections", L.T("Connections to SharePoint (Entra apps)")) })
{
    var group = new Command(kind, title);
    group.Subcommands.Add(Cmd("list", L.T("List"), (_, h) => h.ListSettings(kind)));
    group.Subcommands.Add(Cmd("get", L.T("Show or export to JSON"), (p, h) => h.GetSetting(kind, p.GetValue(nameArgument)!, p.GetValue(outputFile)), nameArgument, outputFile));
    group.Subcommands.Add(Cmd("import", L.T("Save from a JSON file (create or replace)"), (p, h) => h.PutSetting(kind, p.GetValue(fileArgument)!, p.GetValue(importName)), fileArgument, importName));
    group.Subcommands.Add(Cmd("delete", L.T("Delete"), (p, h) => h.DeleteSetting(kind, p.GetValue(nameArgument)!), nameArgument));
    if (kind == "profiles")
        group.Subcommands.Add(Cmd("validate", L.T("Validate the profile as before a run"), (p, h) => h.ValidateProfile(p.GetValue(nameArgument)!), nameArgument));
    if (kind == "connections")
        group.Subcommands.Add(Cmd("test", L.T("Get a token and check access to the profile's libraries"), (p, h) => h.TestConnection(p.GetValue(nameArgument)!, p.GetValue(profileOption)), nameArgument, profileOption));
    root.Subcommands.Add(group);
}

var mapProfile = new Argument<string>("profile") { Description = L.T("Profile") };
var mapPaths = new Argument<string[]>("path") { Description = L.T("Paths in the source library"), Arity = ArgumentArity.OneOrMore };
var mapFile = new Argument<string>("file") { Description = L.T("CSV, TSV or XLSX: source, library, path, comment") };
var mapOut = new Option<string?>("--output", "-o") { Description = L.T("Folder for the mapping reports (check) or a CSV file (export)") };
var appendOption = new Option<bool>("--append") { Description = L.T("Append the rows to the existing ones instead of replacing them") };
var map = new Command("map", L.T("Mapping: where each part of the source moves"));
map.Subcommands.Add(Cmd("test", L.T("Where a file or folder will go"), (p, h) => h.MapTest(p.GetValue(mapProfile)!, p.GetValue(mapPaths)!), mapProfile, mapPaths));
map.Subcommands.Add(Cmd("check", L.T("Mapping coverage by the last inventory: what goes where, what stays, conflicts, paths too long"),
    (p, h) => h.MapCheck(p.GetValue(mapProfile)!, p.GetValue(mapOut), interrupt.Token), mapProfile, mapOut));
map.Subcommands.Add(Cmd("import", L.T("Load mapping rows into the profile"), (p, h) => h.MapImport(p.GetValue(mapProfile)!, p.GetValue(mapFile)!, p.GetValue(appendOption)),
    mapProfile, mapFile, appendOption));
var exportFile = new Option<string>("--output", "-o") { Description = L.T("CSV file"), Required = true };
map.Subcommands.Add(Cmd("export", L.T("Export the profile's mapping to CSV"), (p, h) => h.MapExport(p.GetValue(mapProfile)!, p.GetValue(exportFile)!), mapProfile, exportFile));
root.Subcommands.Add(map);

var planProfile = new Argument<string>("profile") { Description = L.T("Profile") };
var planId = new Argument<long>("plan") { Description = L.T("Plan number") };
var planOut = new Option<string?>("--output", "-o") { Description = L.T("Folder for the plan reports (CSV)") };
var plan = new Command("plan", L.T("Plan: batches, waves, folders to create and forecast (moves nothing)"));
plan.Subcommands.Add(Cmd("build", L.T("Build a plan from the last inventory and the profile's mapping"),
    (p, h) => h.PlanBuild(p.GetValue(planProfile)!, p.GetValue(planOut), interrupt.Token), planProfile, planOut));
plan.Subcommands.Add(Cmd("show", L.T("Show a saved plan"),
    (p, h) => h.PlanShow(p.GetValue(planId), p.GetValue(planOut), interrupt.Token), planId, planOut));
root.Subcommands.Add(plan);

var checkOut = new Option<string?>("--output", "-o") { Description = L.T("CSV file with the findings") };
root.Subcommands.Add(Cmd("check-targets", L.T("Check the profile's target libraries before a move: columns, content types, check-out, approval, drafts (changes nothing)"),
    (p, h) => h.CheckTargets(p.GetValue(planProfile)!, p.GetValue(checkOut), interrupt.Token), planProfile, checkOut));

var runProfile = new Argument<string>("profile") { Description = L.T("Run profile") };
var waitOption = new Option<bool>("--wait", "-w") { Description = L.T("Wait for completion, show progress") };
var yesOption = new Option<bool>("--yes", "-y") { Description = L.T("Do not ask for confirmation (deleting a run)") };
var updateOption = new Option<bool>("--update", "-u") { Description = L.T("Only changes since the profile's last completed inventory (delta)") };
// Inventory (plan is a separate command); apply, verify and retry come with stage 4.
root.Subcommands.Add(Cmd("inventory", L.T("Inventory: list all files of the profile's libraries (no changes in SharePoint)"),
    (p, h) => h.StartRun("inventory", p.GetValue(runProfile)!, p.GetValue(waitOption), interrupt.Token, p.GetValue(updateOption)), runProfile, waitOption, updateOption));

var applyPlan = new Option<long?>("--plan") { Description = L.T("Plan number (default — the profile's newest plan)") };
root.Subcommands.Add(Cmd("apply", L.T("Move: execute the profile's plan (changes SharePoint)"),
    (p, h) => h.Apply(p.GetValue(runProfile)!, p.GetValue(applyPlan), p.GetValue(waitOption), interrupt.Token), runProfile, applyPlan, waitOption));

var runArgument = new Argument<long>("run") { Description = L.T("Run number") };
var takeOption = new Option<int>("--take") { Description = L.T("How many to show"), DefaultValueFactory = _ => 20 };
root.Subcommands.Add(Cmd("runs", L.T("List of runs"), (p, h) => h.Runs(p.GetValue(takeOption)), takeOption));
root.Subcommands.Add(Cmd("status", L.T("Run status"), (p, h) => h.Status(p.GetValue(runArgument)), runArgument));
root.Subcommands.Add(Cmd("wait", L.T("Wait for the run to finish"), (p, h) => h.Wait(p.GetValue(runArgument), interrupt.Token), runArgument));
foreach (var (action, description) in new[]
{
    ("start", L.T("Continue a stopped or aborted run")),
    ("pause", L.T("Pause: no new listing pages are requested")),
    ("resume", L.T("Continue after a pause")),
    ("stop", L.T("Stop, keeping the position; the run can be continued with the start command")),
})
{
    root.Subcommands.Add(Cmd(action, description, (p, h) => h.Control(p.GetValue(runArgument), action, p.GetValue(waitOption), interrupt.Token), runArgument, waitOption));
}

root.Subcommands.Add(Cmd("delete-run", L.T("Delete a run that is not running, with all its results (nothing changes in SharePoint)"),
    (p, h) => h.DeleteRun(p.GetValue(runArgument), p.GetValue(yesOption), interrupt.Token), runArgument, yesOption));

var fileStatusOption = new Option<string?>("--status") { Description = L.T("Status (comma-separated)") };
var fileClassOption = new Option<string?>("--class") { Description = L.T("Error class") };
var searchOption = new Option<string?>("--search") { Description = L.T("Part of the path") };
root.Subcommands.Add(Cmd("files", L.T("Files of a run"), (p, h) => h.Files(p.GetValue(runArgument), p.GetValue(fileStatusOption), p.GetValue(fileClassOption), p.GetValue(searchOption), p.GetValue(takeOption)),
    runArgument, fileStatusOption, fileClassOption, searchOption, takeOption));

var fileIdArgument = new Argument<long>("file") { Description = L.T("File number") };
var commentOption = new Option<string>("--comment") { Description = L.T("Why the file is ignored"), Required = true };
root.Subcommands.Add(Cmd("ignore", L.T("Do not include the file in further retries"), (p, h) => h.Ignore(p.GetValue(fileIdArgument), p.GetValue(commentOption)!), fileIdArgument, commentOption));

var verifyUpdate = new Option<bool>("--update", "-u") { Description = L.T("First update the inventory with changes only") };
var verifyOut = new Option<string?>("--output", "-o") { Description = L.T("Folder for the verify report (CSV)") };
root.Subcommands.Add(Cmd("verify", L.T("Verify the move against an inventory made after it: everything in the targets, nothing in the source"),
    (p, h) => h.Verify(p.GetValue(runArgument), p.GetValue(verifyUpdate), p.GetValue(verifyOut), interrupt.Token), runArgument, verifyUpdate, verifyOut));
var retryClass = new Option<string[]>("--class") { Description = L.T("Error class (conflict, locked, transient, verify, …); may be repeated"), AllowMultipleArgumentsPerToken = true };
var retryFile = new Option<long[]>("--file") { Description = L.T("File number of the run; may be repeated"), AllowMultipleArgumentsPerToken = true };
root.Subcommands.Add(Cmd("retry", L.T("Retry moving failed or deferred files of an apply run (without conditions — all of them)"),
    (p, h) => h.Retry(p.GetValue(runArgument), p.GetValue(retryClass) ?? [], p.GetValue(retryFile) ?? [], p.GetValue(waitOption), interrupt.Token), runArgument, retryClass, retryFile, waitOption));
var rulesFile = new Option<string>("--output", "-o") { Description = L.T("CSV file"), Required = true };
root.Subcommands.Add(Cmd("export-rules", L.T("Link replacement rules for SPOReplaceLink from an apply run (CSV: old,new,name)"),
    (p, h) => h.ExportRules(p.GetValue(runArgument), p.GetValue(rulesFile)!, interrupt.Token), runArgument, rulesFile));

root.Subcommands.Add(Cmd("cleanup-folders", L.T("After the move is verified: delete to the site recycle bin the source folders it emptied (only empty ones, within the mapping rules)"),
    (p, h) => h.CleanupFolders(p.GetValue(runArgument), p.GetValue(yesOption), p.GetValue(waitOption), interrupt.Token), runArgument, yesOption, waitOption));

var pilotFile = new Option<string?>("--output", "-o") { Description = L.T("Save to a TXT file") };
root.Subcommands.Add(Cmd("pilot-report", L.T("Pilot report of an apply run: speed per batch and overall, RU, 429/503, locks, verify, forecast for the rest"),
    (p, h) => h.PilotReport(p.GetValue(runArgument), p.GetValue(pilotFile), interrupt.Token), runArgument, pilotFile));

var exportDir = new Option<string>("--output", "-o") { Description = L.T("Folder for the reports"), DefaultValueFactory = _ => "." };
root.Subcommands.Add(Cmd("export", L.T("Run reports: files, folders, checkouts, attention, summary"),
    (p, h) => h.Export(p.GetValue(runArgument), p.GetValue(exportDir)!), runArgument, exportDir));

return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null });
