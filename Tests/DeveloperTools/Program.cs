using FullStackLauncher.Models;
using FullStackLauncher.Services;

// These checks resolve/validate paths only. They never open applications, databases, or login pages.
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    checks++;
    Console.WriteLine("PASS: " + description);
}
void Reject(Action action, string description)
{
    try { action(); }
    catch (ArgumentException) { Check(true, description); return; }
    throw new Exception("FAIL: " + description + " was accepted");
}

var root = Path.Combine(Path.GetTempPath(), "FullStackLauncher-DeveloperTools-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var executable = Path.Combine(root, "Fixture App.exe");
File.WriteAllText(executable, "This is a validation fixture, never executed.");
Environment.SetEnvironmentVariable("FULLSTACK_LAUNCHER_TEST_TOOLS", root);
try
{
    var website = new DeveloperTool { Name = "Documentation", Kind = "Website", Target = "https://www.postgresql.org/docs/" };
    DeveloperToolLauncher.Validate(website, root, true);
    var resolved = DeveloperToolLauncher.Resolve(website, root);
    Check(resolved.IsWebsite && resolved.FileName == website.Target, "external HTTPS documentation URL supported");
    website.Target = "http://localhost:5050/";
    Check(DeveloperToolLauncher.Resolve(website, root).IsWebsite, "local HTTP website supported");
    foreach (var invalid in new[] { "javascript:alert(1)", "file:///C:/Windows/notepad.exe", "data:text/html,test", "ftp://example.com", "example.com", "https://user:password@example.com", "https://user@example.com", "https://example.com/\nnext" })
    {
        website.Target = invalid;
        Reject(() => DeveloperToolLauncher.Validate(website, root), "unsafe or invalid website rejected: " + invalid.Split(':')[0]);
    }
    Reject(() => DeveloperToolLauncher.Open(new("javascript:alert(1)", true, "unsafe")), "Open independently rejects unsupported browser schemes");
    Reject(() => DeveloperToolLauncher.Open(new("https://user:password@example.com/", true, "unsafe")), "Open independently rejects embedded credentials");

    var application = new DeveloperTool { Name = "Fixture", Kind = "Application", Target = "Fixture App.exe" };
    DeveloperToolLauncher.Validate(application, root, true);
    resolved = DeveloperToolLauncher.Resolve(application, root);
    Check(!resolved.IsWebsite && resolved.FileName == executable, "relative executable resolved from settings directory");
    application.Target = "%FULLSTACK_LAUNCHER_TEST_TOOLS%\\Fixture App.exe";
    Check(DeveloperToolLauncher.Resolve(application, root).FileName == executable, "environment variables expanded in executable path");
    application.Target = executable;
    Check(DeveloperToolLauncher.Resolve(application, root).FileName == executable, "absolute executable path retained");
    foreach (var invalid in new[] { "start.cmd", "script.ps1", "Fixture App.exe --argument", "cmd.exe", "powershell.exe", "pwsh.exe", "\"Fixture App.exe\"", "%FULLSTACK_LAUNCHER_UNDEFINED_VARIABLE%\\app.exe", "app.exe\n", "https://example.com/app.exe" })
    {
        application.Target = invalid;
        Reject(() => DeveloperToolLauncher.Validate(application, root), "non-executable/command/invalid application path rejected");
    }
    application.Target = "missing.exe";
    DeveloperToolLauncher.Validate(application, root);
    Check(true, "portable settings can be validated before executable is installed");
    Reject(() => DeveloperToolLauncher.Validate(application, root, true), "interactive save can require executable to exist");
    try { DeveloperToolLauncher.Resolve(application, root); throw new Exception("Missing application resolved."); }
    catch (FileNotFoundException ex) { Check(ex.Message.Contains("Edit this tool"), "missing executable produces actionable resolve error"); }
    application.Kind = "Unknown";
    Reject(() => DeveloperToolLauncher.Validate(application, root), "unknown tool kind rejected");
    application.Kind = "Application";
    application.Name = " ";
    Reject(() => DeveloperToolLauncher.Validate(application, root), "blank tool name rejected");

    var pgAdmin = DeveloperTool.PgAdmin();
    DeveloperToolLauncher.Validate(pgAdmin, root, true);
    Check(true, "default pgAdmin tool supports automatic discovery");
    pgAdmin.Target = executable;
    Check(DeveloperToolLauncher.Resolve(pgAdmin, root).FileName == executable, "pgAdmin executable override supported");
    pgAdmin.Target = "";
    try
    {
        var pgAdminTarget = DeveloperToolLauncher.Resolve(pgAdmin, root);
        Check(!pgAdminTarget.IsWebsite && File.Exists(pgAdminTarget.FileName)
            && Path.GetFileName(pgAdminTarget.FileName).Equals("pgAdmin4.exe", StringComparison.OrdinalIgnoreCase), "installed pgAdmin desktop executable discovered");
        Console.WriteLine("Resolved pgAdmin executable: " + pgAdminTarget.FileName);
    }
    catch (FileNotFoundException ex)
    {
        Check(ex.Message.Contains("Install pgAdmin 4"), "absent pgAdmin has actionable installation guidance");
    }
    Console.WriteLine($"All {checks} developer-tool checks passed.");
}
finally
{
    Environment.SetEnvironmentVariable("FULLSTACK_LAUNCHER_TEST_TOOLS", null);
    File.Delete(executable);
    Directory.Delete(root);
}
