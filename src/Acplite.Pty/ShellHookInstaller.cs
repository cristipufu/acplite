using System.Text;

namespace Acplite.Pty;

public sealed record ShellHookStatus(
    string ShellName,
    string ProfilePath,
    bool IsInstalled);

/// <summary>
/// Installs, inspects, and removes transparent shell wrapper functions for <c>claude</c>,
/// <c>codex</c>, <c>grok</c>, and <c>agy</c> across <c>zsh</c>, <c>bash</c>, <c>fish</c>,
/// PowerShell 7 (<c>pwsh</c>), and Windows PowerShell (<c>powershell</c>).
/// </summary>
public static class ShellHookInstaller
{
    public const string StartMarker = "# >>> acplite pty hooks >>>";
    public const string EndMarker = "# <<< acplite pty hooks <<<";

    public static IReadOnlyList<string> DefaultProviders { get; } = ["claude", "codex", "grok", "agy"];

    private static readonly string[] SupportedShells = ["zsh", "bash", "fish", "pwsh", "powershell"];

    public static IReadOnlyList<string> ParseProvidersList(string? agentsCsv)
    {
        if (string.IsNullOrWhiteSpace(agentsCsv))
        {
            return DefaultProviders;
        }

        var list = new List<string>();
        foreach (var raw in agentsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleaned = ValidateAgentName(raw);
            if (!list.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(cleaned);
            }
        }

        return list.Count > 0 ? list : DefaultProviders;
    }

    public static string DetectDefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return "pwsh";
        }

        var shellEnv = Environment.GetEnvironmentVariable("SHELL") ?? string.Empty;
        if (shellEnv.EndsWith("/zsh", StringComparison.OrdinalIgnoreCase))
        {
            return "zsh";
        }
        if (shellEnv.EndsWith("/bash", StringComparison.OrdinalIgnoreCase))
        {
            return "bash";
        }
        if (shellEnv.EndsWith("/fish", StringComparison.OrdinalIgnoreCase))
        {
            return "fish";
        }

        return OperatingSystem.IsMacOS() ? "zsh" : "bash";
    }

    public static string ResolveProfilePath(string shellName, string? homeDirectoryOverride = null)
    {
        var home = !string.IsNullOrWhiteSpace(homeDirectoryOverride)
            ? homeDirectoryOverride
            : PtySessionRegistry.GetUserHomeDirectory();

        var docs = !string.IsNullOrWhiteSpace(homeDirectoryOverride)
            ? Path.Combine(home, "Documents")
            : (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } sysDocs
                ? sysDocs
                : Path.Combine(home, "Documents"));

        return NormalizeShellName(shellName) switch
        {
            "zsh" => Path.Combine(home, ".zshrc"),
            "bash" => Path.Combine(home, ".bashrc"),
            "fish" => Path.Combine(home, ".config", "fish", "config.fish"),
            "pwsh" => OperatingSystem.IsWindows()
                ? Path.Combine(docs, "PowerShell", "Microsoft.PowerShell_profile.ps1")
                : Path.Combine(home, ".config", "powershell", "Microsoft.PowerShell_profile.ps1"),
            "powershell" => Path.Combine(docs, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"),
            _ => throw new ArgumentException($"Unsupported shell '{shellName}'. Expected: zsh, bash, fish, pwsh, or powershell.", nameof(shellName))
        };
    }

    public static async Task<IReadOnlyList<ShellHookStatus>> GetAllStatusesAsync(
        string? homeDirectoryOverride = null,
        CancellationToken cancellationToken = default)
    {
        var list = new List<ShellHookStatus>(SupportedShells.Length);
        foreach (var shell in SupportedShells)
        {
            var path = ResolveProfilePath(shell, homeDirectoryOverride);
            var installed = false;
            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                installed = ContainsHookBlock(content);
            }
            list.Add(new ShellHookStatus(shell, path, installed));
        }
        return list;
    }

    public static async Task<ShellHookStatus> InstallAsync(
        string? shellName = null,
        string? homeDirectoryOverride = null,
        string wrapperCommand = "acplite pty",
        IReadOnlyList<string>? providers = null,
        CancellationToken cancellationToken = default)
    {
        var targetShell = NormalizeShellName(shellName ?? DetectDefaultShell());
        var profilePath = ResolveProfilePath(targetShell, homeDirectoryOverride);
        var snippet = BuildHookSnippet(targetShell, wrapperCommand, providers);

        var parentDir = Path.GetDirectoryName(profilePath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var existing = File.Exists(profilePath)
            ? await File.ReadAllTextAsync(profilePath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        var updated = UpsertHookBlock(existing, snippet);
        await File.WriteAllTextAsync(profilePath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);

        return new ShellHookStatus(targetShell, profilePath, true);
    }

    public static async Task<ShellHookStatus> UninstallAsync(
        string? shellName = null,
        string? homeDirectoryOverride = null,
        CancellationToken cancellationToken = default)
    {
        var targetShell = NormalizeShellName(shellName ?? DetectDefaultShell());
        var profilePath = ResolveProfilePath(targetShell, homeDirectoryOverride);

        if (File.Exists(profilePath))
        {
            var existing = await File.ReadAllTextAsync(profilePath, cancellationToken).ConfigureAwait(false);
            var updated = RemoveHookBlock(existing);
            if (!string.Equals(existing, updated, StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(profilePath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            }
        }

        return new ShellHookStatus(targetShell, profilePath, false);
    }

    public static string BuildHookSnippet(
        string shellName,
        string wrapperCommand = "acplite pty",
        IReadOnlyList<string>? providers = null)
    {
        var parts = wrapperCommand.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var binary = parts.Length > 0 ? parts[0] : "acplite";
        var subcmd = parts.Length > 1 ? parts[1] : "pty";
        var targetProviders = providers is { Count: > 0 } ? providers : DefaultProviders;
        var validatedProviders = targetProviders.Select(ValidateAgentName).ToArray();

        var posixFunctions = string.Join(
            "\n",
            validatedProviders.Select(static p => $"{p}() {{ _acplite_pty_wrap {p} \"$@\"; }}"));
        var fishFunctions = string.Join(
            "\n",
            validatedProviders.Select(static p => $"function {p}; _acplite_pty_wrap {p} $argv; end"));
        var pwshFunctions = string.Join(
            "\n",
            validatedProviders.Select(static p => $"function {p} {{ Invoke-AcplitePtyWrap '{p}' $args }}"));

        return NormalizeShellName(shellName) switch
        {
            "zsh" or "bash" => $$"""
{{StartMarker}}
_acplite_pty_wrap() {
  local agent="$1"; shift
  if [ ! -t 0 ] || [ ! -t 1 ] || [ -n "$ACPLITE_PTY_ACTIVE" ]; then
    command "$agent" "$@"
    return $?
  fi
  for arg in "$@"; do
    case "$arg" in
      -p|--print|-h|--help|-v|--version|mcp|serve|exec|acp)
        command "$agent" "$@"
        return $?
        ;;
    esac
  done
  if command -v {{binary}} >/dev/null 2>&1; then
    {{binary}} {{subcmd}} "$agent" -- "$@"
  elif command -v acplite >/dev/null 2>&1; then
    acplite pty "$agent" -- "$@"
  elif command -v tunnelite >/dev/null 2>&1; then
    tunnelite acp-pty "$agent" -- "$@"
  else
    command "$agent" "$@"
  fi
}
{{posixFunctions}}
{{EndMarker}}
""".Replace("\r\n", "\n", StringComparison.Ordinal),

            "fish" => $$"""
{{StartMarker}}
function _acplite_pty_wrap
    set -l agent $argv[1]
    set -e argv[1]
    if not isatty stdin; or not isatty stdout; or set -q ACPLITE_PTY_ACTIVE
        command $agent $argv
        return $status
    end
    for arg in $argv
        switch $arg
            case -p --print -h --help -v --version mcp serve exec acp
                command $agent $argv
                return $status
        end
    end
    if command -q {{binary}}
        {{binary}} {{subcmd}} $agent -- $argv
    else if command -q acplite
        acplite pty $agent -- $argv
    else if command -q tunnelite
        tunnelite acp-pty $agent -- $argv
    else
        command $agent $argv
    end
end
{{fishFunctions}}
{{EndMarker}}
""".Replace("\r\n", "\n", StringComparison.Ordinal),

            "pwsh" or "powershell" => $$"""
{{StartMarker}}
function Invoke-AcplitePtyWrap {
    param([string]$Agent, [string[]]$RemainingArgs)
    if ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected -or $env:ACPLITE_PTY_ACTIVE) {
        & (Get-Command $Agent -CommandType Application | Select-Object -First 1) @RemainingArgs
        return
    }
    foreach ($a in $RemainingArgs) {
        if ($a -in @('-p','--print','-h','--help','-v','--version','mcp','serve','exec','acp')) {
            & (Get-Command $Agent -CommandType Application | Select-Object -First 1) @RemainingArgs
            return
        }
    }
    if (Get-Command {{binary}} -ErrorAction SilentlyContinue) {
        & {{binary}} {{subcmd}} $Agent -- @RemainingArgs
    } elseif (Get-Command acplite -ErrorAction SilentlyContinue) {
        & acplite pty $Agent -- @RemainingArgs
    } elseif (Get-Command tunnelite -ErrorAction SilentlyContinue) {
        & tunnelite acp-pty $Agent -- @RemainingArgs
    } else {
        & (Get-Command $Agent -CommandType Application | Select-Object -First 1) @RemainingArgs
    }
}
{{pwshFunctions}}
{{EndMarker}}
""".Replace("\r\n", "\n", StringComparison.Ordinal),

            _ => throw new ArgumentException($"Unsupported shell '{shellName}'.", nameof(shellName))
        };
    }

    private static string ValidateAgentName(string raw)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("Agent command name cannot be empty.", nameof(raw));
        }

        foreach (var ch in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-')
            {
                throw new ArgumentException($"Invalid character '{ch}' in agent command name '{trimmed}'.", nameof(raw));
            }
        }

        return trimmed;
    }

    private static string NormalizeShellName(string shellName)
    {
        var lower = shellName.Trim().ToLowerInvariant();
        return lower switch
        {
            "zsh" => "zsh",
            "bash" => "bash",
            "fish" => "fish",
            "pwsh" => "pwsh",
            "powershell" => "powershell",
            _ => throw new ArgumentException($"Unsupported shell '{shellName}'. Expected: zsh, bash, fish, pwsh, or powershell.", nameof(shellName))
        };
    }

    private static bool ContainsHookBlock(string content)
        => content.Contains(StartMarker, StringComparison.Ordinal) &&
           content.Contains(EndMarker, StringComparison.Ordinal);

    private static string UpsertHookBlock(string existingContent, string hookSnippet)
    {
        var cleaned = RemoveHookBlock(existingContent).TrimEnd();
        if (string.IsNullOrEmpty(cleaned))
        {
            return hookSnippet + "\n";
        }

        return cleaned + "\n\n" + hookSnippet + "\n";
    }

    private static string RemoveHookBlock(string content)
    {
        var startIdx = content.IndexOf(StartMarker, StringComparison.Ordinal);
        if (startIdx < 0)
        {
            return content;
        }

        var endIdx = content.IndexOf(EndMarker, startIdx, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            return content;
        }

        var removeUntil = endIdx + EndMarker.Length;
        while (removeUntil < content.Length && (content[removeUntil] == '\r' || content[removeUntil] == '\n'))
        {
            removeUntil++;
        }

        var before = content[..startIdx].TrimEnd();
        var after = content[removeUntil..].TrimStart('\r', '\n');

        if (string.IsNullOrEmpty(before))
        {
            return after;
        }
        if (string.IsNullOrEmpty(after))
        {
            return before + "\n";
        }
        return before + "\n\n" + after;
    }
}
