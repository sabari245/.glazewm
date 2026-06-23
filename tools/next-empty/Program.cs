// Jumps to the first empty GlazeWM workspace (in config order 1..9).
// Built as a Windows-subsystem exe so it never spawns a console window.
//
// Usage:
//   next-empty-workspace.exe            -> just focus the next empty workspace
//   next-empty-workspace.exe --move     -> move the focused window there too,
//                                          then follow it
//
// Logic: GlazeWM only reports a workspace in `query workspaces` once it is
// active/displayed, and an empty-but-displayed workspace has no children. So a
// workspace is "occupied" iff it appears in the query with >0 children. The
// first candidate name that isn't occupied is the next empty workspace.
//
// Note on --move: the window is moved first, then focused. The source workspace
// only becomes empty-and-destroyed once we focus away, so the workspace-compactor
// daemon's renumbering happens after both commands — and since relabelling keeps
// focus, we always land correctly with no empty middle workspace.

using System.Diagnostics;
using System.Text.Json;

const string Cli = @"C:\Program Files\glzr.io\GlazeWM\cli\glazewm.exe";

bool moveWindow = args.Length > 0 && args[0] == "--move";

// Candidate workspaces, in the order they're defined in config.yaml.
string[] candidates = { "1", "2", "3", "4", "5", "6", "7", "8", "9" };

static string Run(string exe, string args)
{
    var psi = new ProcessStartInfo
    {
        FileName = exe,
        Arguments = args,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    using var p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return output;
}

string json = Run(Cli, "query workspaces");

var occupied = new HashSet<string>();
using (var doc = JsonDocument.Parse(json))
{
    var workspaces = doc.RootElement.GetProperty("data").GetProperty("workspaces");
    foreach (var ws in workspaces.EnumerateArray())
    {
        string name = ws.GetProperty("name").GetString() ?? "";
        if (ws.GetProperty("children").GetArrayLength() > 0)
            occupied.Add(name);
    }
}

foreach (var candidate in candidates)
{
    if (!occupied.Contains(candidate))
    {
        if (moveWindow)
            Run(Cli, $"command move --workspace {candidate}");
        Run(Cli, $"command focus --workspace {candidate}");
        return;
    }
}
// All workspaces occupied: do nothing.
