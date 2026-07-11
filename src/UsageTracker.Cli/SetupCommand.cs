using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsageTracker.Cli;

/// <summary>
/// Generates the hook config files for a coding agent so users don't hand-edit JSON. `setup claude`
/// merges the command-hook block into <c>.claude/settings.json</c> (preserving unrelated settings);
/// `setup github` writes the Copilot loopback-HTTP hook file. Both write into the current directory.
/// </summary>
public static class SetupCommand
{
    // Events that carry a tool matcher vs. those that don't (Claude Code schema).
    private static readonly string[] MatcherEvents = ["PreToolUse", "PostToolUse", "PostToolUseFailure"];
    private static readonly string[] PlainEvents =
        ["SessionStart", "SessionEnd", "UserPromptSubmit", "Stop", "SubagentStart", "SubagentStop", "PreCompact"];

    // GitHub Copilot's canonical camelCase event names (PascalCase aliases also work for VS Code).
    private static readonly string[] CopilotEvents =
        ["sessionStart", "sessionEnd", "userPromptSubmitted", "preToolUse", "postToolUse", "errorOccurred"];

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: usagetracker setup <claude|github>");
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "claude" or "claude-code" => SetupClaude(),
            "github" or "copilot" => SetupGithub(),
            var other => Unknown(other),
        };
    }

    private static int SetupClaude()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), ".claude", "settings.json");
        var root = LoadObject(path);

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        const string command = "usagetracker command claude-code --stdin";
        foreach (var evt in MatcherEvents)
            hooks[evt] = new JsonArray(new JsonObject
            {
                ["matcher"] = "*",
                ["hooks"] = new JsonArray(HookCommand(command)),
            });
        foreach (var evt in PlainEvents)
            hooks[evt] = new JsonArray(new JsonObject
            {
                ["hooks"] = new JsonArray(HookCommand(command)),
            });

        Write(path, root);
        Console.WriteLine($"Wrote Claude Code hooks to {path}");
        Console.WriteLine("Identity comes from the daemon's Entra token; no USER_EMAIL needed.");
        Console.WriteLine("Note: existing hooks for these events were replaced; other settings were preserved.");
        return 0;
    }

    private static int SetupGithub()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), ".github", "hooks", "usage-tracking.json");

        // Copilot delivers each event as JSON on stdin, so a command hook pipes it to the CLI - the same
        // path Claude Code uses. No HTTP listener or local token is needed.
        const string command = "usagetracker command copilot --stdin";
        var hooks = new JsonObject();
        foreach (var evt in CopilotEvents)
            hooks[evt] = new JsonArray(CopilotCommandHook(command));

        var root = new JsonObject
        {
            ["version"] = 1,
            ["hooks"] = hooks,
        };

        Write(path, root);
        Console.WriteLine($"Wrote Copilot hooks to {path}");
        Console.WriteLine("Requires 'usagetracker' on PATH (run 'usagetracker init' first). Identity comes from the daemon's Entra token.");
        Console.WriteLine("Repo-level hooks must be on the default branch to apply to the Copilot cloud agent.");
        return 0;
    }

    private static JsonObject HookCommand(string command) => new()
    {
        ["type"] = "command",
        ["command"] = command,
        ["timeout"] = 10,
    };

    private static JsonObject CopilotCommandHook(string command) => new()
    {
        ["type"] = "command",
        ["bash"] = command,
        ["powershell"] = command,
        ["timeoutSec"] = 10,
    };

    private static JsonObject LoadObject(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"warning: {path} was not valid JSON; replacing it.");
        }
        return new JsonObject();
    }

    private static void Write(string path, JsonNode root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int Unknown(string target)
    {
        Console.Error.WriteLine($"unknown setup target: {target} (expected 'claude' or 'github')");
        return 1;
    }
}
