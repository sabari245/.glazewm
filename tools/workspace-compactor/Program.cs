// GlazeWM workspace compactor (daemon).
//
// Keeps OCCUPIED workspaces numbered contiguously from 1. When a workspace is
// emptied and destroyed (e.g. you close its last window and switch away), the
// remaining occupied workspaces are re-labelled to close the gap:
//     occupied [1, 3]            ->  [1, 2]
//     occupied [2, 4]            ->  [1, 2]
//
// Relabelling uses `update-workspace-config --name`, which keeps the same
// underlying workspace object. So if you were standing on workspace "3" when it
// becomes "2", focus follows you automatically — no extra handling needed.
//
// Runs as a silent background process (WinExe = no console window). Launched
// from `startup_commands` in config.yaml. Single-instance via a named mutex.

using System.Diagnostics;
using System.Text.Json;

const string Cli = @"C:\Program Files\glzr.io\GlazeWM\cli\glazewm.exe";

string LogPath = Path.Combine(AppContext.BaseDirectory, "compactor.log");

// Keep the log from growing without bound across sessions.
try { if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024) File.Delete(LogPath); }
catch { }

void Log(string msg)
{
    try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}"); }
    catch { }
}

// Ensure only one daemon runs at a time (config reloads / restarts are safe).
using var mutex = new Mutex(initiallyOwned: true, "Global\\GlazeWmWorkspaceCompactor", out bool isNew);
if (!isNew)
    return; // Another instance already owns it.

Log("daemon started");

// Reconnect loop: survives transient drops; exits cleanly on WM shutdown.
while (true)
{
    Process? sub = null;
    try
    {
        sub = StartSub();
        string? line;
        while ((line = sub.StandardOutput.ReadLine()) != null)
        {
            string? eventType = TryGetEventType(line);
            if (eventType == "application_exiting")
            {
                Log("application_exiting received; daemon stopping");
                return; // WM is shutting down; stop the daemon too.
            }
            if (eventType == "workspace_deactivated")
            {
                Log("workspace_deactivated received");
                Compact(); // Idempotent; safe to run on every deactivation.
            }
        }
    }
    catch
    {
        // fall through to reconnect
    }
    finally
    {
        try { sub?.Kill(); } catch { }
        sub?.Dispose();
    }

    // Subscription ended (likely WM restart/crash). Wait, then reconnect.
    Thread.Sleep(2000);
}

// --- helpers ---------------------------------------------------------------

static Process StartSub()
{
    var psi = new ProcessStartInfo
    {
        FileName = Cli,
        // Both events stream on the same subscription, one JSON object per line.
        Arguments = "sub -e workspace_deactivated application_exiting",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    return Process.Start(psi)!;
}

static string? TryGetEventType(string line)
{
    try
    {
        using var doc = JsonDocument.Parse(line);
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("eventType", out var et))
        {
            return et.GetString();
        }
    }
    catch { }
    return null;
}

static string RunCli(string args)
{
    var psi = new ProcessStartInfo
    {
        FileName = Cli,
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

// Re-label occupied workspaces to be contiguous from 1, in ascending order.
void Compact()
{
    string json = RunCli("query workspaces");

    var occupied = new List<string>();           // names of occupied workspaces
    var inUse = new HashSet<string>();            // names of ALL active workspaces

    try
    {
        using var doc = JsonDocument.Parse(json);
        var workspaces = doc.RootElement.GetProperty("data").GetProperty("workspaces");
        foreach (var ws in workspaces.EnumerateArray())
        {
            string name = ws.GetProperty("name").GetString() ?? "";
            inUse.Add(name);
            if (ws.GetProperty("children").GetArrayLength() > 0)
                occupied.Add(name);
        }
    }
    catch
    {
        return; // Malformed/empty query result; skip this round.
    }

    // Sort occupied numerically (non-numeric names sort last, stable).
    occupied.Sort((a, b) => ParseRank(a).CompareTo(ParseRank(b)));

    Log($"compact: occupied=[{string.Join(",", occupied)}] active=[{string.Join(",", inUse)}]");

    for (int i = 0; i < occupied.Count; i++)
    {
        string current = occupied[i];
        string target = (i + 1).ToString();
        if (current == target)
            continue;

        // Never create a duplicate: if some OTHER active workspace still holds
        // the target name (e.g. an empty workspace you're currently sitting on),
        // skip this round rather than collide. Ascending order normally frees
        // the target name before we need it.
        if (inUse.Contains(target))
        {
            Log($"  skip {current}->{target} (target name in use)");
            continue;
        }

        Log($"  rename {current}->{target}");
        RunCli($"command update-workspace-config --workspace {current} --name {target}");
        inUse.Remove(current);
        inUse.Add(target);
    }
}

static int ParseRank(string name) =>
    int.TryParse(name, out int n) ? n : int.MaxValue;
