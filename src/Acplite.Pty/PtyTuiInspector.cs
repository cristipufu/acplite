using System.Text;

namespace Acplite.Pty;

/// <summary>
/// Inspects a rolling window of ANSI-stripped terminal output from an interactive coding agent
/// (<c>agy</c>, <c>claude</c>, <c>codex</c>, <c>grok</c>) to detect active TUI tool-confirmation
/// dialogs and map mobile approval/rejection decisions to the appropriate keystroke sequences.
/// Uses <see cref="SemaphoreSlim"/> exclusively for async coordination.
/// </summary>
public sealed class PtyTuiInspector
{
    private const int MaxRollingChars = 3072;

    private static readonly string[] ConfirmationQuestions =
    [
        "Do you want to proceed?",
        "Do you want to run this command",
        "Do you want to make this edit",
        "Allow execution of",
        "Would you like to run",
        "Allow command?",
        "Approve command?",
        "Run this command?",
        "Execute this command?",
        "Approve tool",
        "Allow tool",
        "Confirm tool execution",
        "Apply these changes?"
    ];

    private static readonly string[] MenuOptionIndicators =
    [
        "Yes, allow once",
        "Yes, allow always",
        "Yes, and don't ask again",
        "No, and tell Claude",
        "No, suggest changes",
        "1. Yes",
        "1) Yes",
        "1. Allow",
        "1) Allow",
        "Approve and run"
    ];

    private static readonly string[] YesNoTokens =
    [
        "[y/n]",
        "(y/n)",
        "[Y/n]",
        "[y/N]",
        "(Y/n)",
        "(y/N)"
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _rollingText = new(MaxRollingChars);
    private PtyPendingPermission? _activePermission;
    private string? _lastDetectedFingerprint;
    private long _requestCounter;

    public PtyPendingPermission? ActivePermission => Volatile.Read(ref _activePermission);

    /// <summary>
    /// Feeds a chunk of raw UTF-8 terminal output into the inspector asynchronously.
    /// Returns a newly detected <see cref="PtyPendingPermission"/> when a tool-approval prompt appears.
    /// </summary>
    public ValueTask<PtyPendingPermission?> ProcessOutputChunkAsync(
        ReadOnlyMemory<byte> utf8Bytes,
        CancellationToken cancellationToken = default)
    {
        if (utf8Bytes.IsEmpty)
        {
            return ValueTask.FromResult<PtyPendingPermission?>(null);
        }

        var decoded = Encoding.UTF8.GetString(utf8Bytes.Span);
        return ProcessOutputTextAsync(decoded, cancellationToken);
    }

    public async ValueTask<PtyPendingPermission?> ProcessOutputTextAsync(
        string rawTerminalText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(rawTerminalText))
        {
            return null;
        }

        var cleaned = StripAnsiAndBoxDrawing(rawTerminalText);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ProcessCleanedTextUnderGate(cleaned);
        }
        finally
        {
            _gate.Release();
        }
    }

    private PtyPendingPermission? ProcessCleanedTextUnderGate(string cleaned)
    {
        _rollingText.Append(cleaned);
        if (_rollingText.Length > MaxRollingChars)
        {
            _rollingText.Remove(0, _rollingText.Length - (MaxRollingChars - 512));
        }

        var snapshot = _rollingText.ToString();
        var detected = TryDetectPermissionPrompt(snapshot, out var fingerprint);
        if (detected is null)
        {
            return null;
        }

        if (string.Equals(_lastDetectedFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        _lastDetectedFingerprint = fingerprint;
        Volatile.Write(ref _activePermission, detected);
        return detected;
    }

    /// <summary>
    /// Clears any active permission prompt asynchronously (for example when the user presses a key
    /// in the desktop terminal or when a mobile permission response is injected) and resets the rolling buffer.
    /// </summary>
    public async ValueTask<PtyPendingPermission?> ClearActivePermissionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ClearUnderGate();
        }
        finally
        {
            _gate.Release();
        }
    }

    private PtyPendingPermission? ClearUnderGate()
    {
        var previous = Volatile.Read(ref _activePermission);
        Volatile.Write(ref _activePermission, null);
        _lastDetectedFingerprint = null;
        _rollingText.Clear();
        return previous;
    }

    /// <summary>
    /// Resolves the keystroke sequence to inject into the PTY master for a permission response.
    /// </summary>
    public string ResolveKeystroke(bool approved, string? selectedOptionId = null)
    {
        var current = Volatile.Read(ref _activePermission);
        if (current is not null && current.Options.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(selectedOptionId))
            {
                var matched = current.Options.FirstOrDefault(o =>
                    string.Equals(o.OptionId, selectedOptionId, StringComparison.OrdinalIgnoreCase));
                if (matched is not null)
                {
                    return matched.Keystroke;
                }
            }

            if (approved)
            {
                var allowOpt = current.Options.FirstOrDefault(o =>
                    o.Kind.StartsWith("allow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(o.OptionId, "allow", StringComparison.OrdinalIgnoreCase));
                if (allowOpt is not null)
                {
                    return allowOpt.Keystroke;
                }
            }
            else
            {
                var denyOpt = current.Options.FirstOrDefault(o =>
                    o.Kind.StartsWith("reject", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(o.OptionId, "deny", StringComparison.OrdinalIgnoreCase));
                if (denyOpt is not null)
                {
                    return denyOpt.Keystroke;
                }
            }
        }

        return approved ? "\r" : "\x1b";
    }

    private PtyPendingPermission? TryDetectPermissionPrompt(string windowText, out string? fingerprint)
    {
        fingerprint = null;

        var rawLines = windowText.Split('\n');
        var lines = new List<string>(20);
        for (var i = rawLines.Length - 1; i >= 0 && lines.Count < 18; i--)
        {
            var trimmed = CollapseWhitespace(rawLines[i]);
            if (trimmed.Length >= 2)
            {
                lines.Add(trimmed);
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        lines.Reverse();

        var triggerLineIndex = -1;
        var isYesNoStyle = false;

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (ContainsAnyIgnoreCase(line, ConfirmationQuestions) ||
                ContainsAnyIgnoreCase(line, MenuOptionIndicators))
            {
                triggerLineIndex = i;
                isYesNoStyle = ContainsAnyIgnoreCase(line, YesNoTokens);
                break;
            }

            if (ContainsAnyIgnoreCase(line, YesNoTokens) &&
                (line.Contains("allow", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("approve", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("run", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("execute", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("proceed", StringComparison.OrdinalIgnoreCase)))
            {
                triggerLineIndex = i;
                isYesNoStyle = true;
                break;
            }
        }

        if (triggerLineIndex < 0)
        {
            return null;
        }

        var contextLines = new List<string>();
        var startIdx = Math.Max(0, triggerLineIndex - 8);
        for (var i = startIdx; i <= triggerLineIndex; i++)
        {
            var candidate = lines[i];
            if (IsChromeOrOptionLine(candidate))
            {
                continue;
            }
            contextLines.Add(candidate);
        }

        var toolName = "Tool Approval";
        string? primaryTarget = null;

        foreach (var ctx in contextLines)
        {
            if (ctx.StartsWith("Bash(", StringComparison.OrdinalIgnoreCase) ||
                ctx.StartsWith("Run(", StringComparison.OrdinalIgnoreCase) ||
                ctx.StartsWith("Command:", StringComparison.OrdinalIgnoreCase) ||
                ctx.StartsWith("$ ", StringComparison.Ordinal))
            {
                toolName = "Bash";
                primaryTarget = ctx;
                break;
            }

            if (ctx.StartsWith("Edit(", StringComparison.OrdinalIgnoreCase) ||
                ctx.StartsWith("Write(", StringComparison.OrdinalIgnoreCase) ||
                ctx.StartsWith("Update(", StringComparison.OrdinalIgnoreCase) ||
                ctx.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
            {
                toolName = "Edit";
                primaryTarget = ctx;
                break;
            }
        }

        primaryTarget ??= contextLines.LastOrDefault() ?? lines[triggerLineIndex];
        if (primaryTarget.Length > 180)
        {
            primaryTarget = primaryTarget[..177] + "...";
        }

        var description = contextLines.Count > 1
            ? string.Join("\n", contextLines)
            : lines[triggerLineIndex];

        fingerprint = $"{toolName}:{primaryTarget}";
        var requestId = $"pty_perm_{Interlocked.Increment(ref _requestCounter)}";

        IReadOnlyList<PtyPermissionOption> options = isYesNoStyle
            ? [
                new PtyPermissionOption("allow", "Allow", "allow_once", "y\r"),
                new PtyPermissionOption("deny", "Deny", "reject_once", "n\r")
              ]
            : [
                new PtyPermissionOption("allow", "Allow", "allow_once", "\r"),
                new PtyPermissionOption("allow_always", "Always Allow", "allow_always", "\x1b[B\r"),
                new PtyPermissionOption("deny", "Deny", "reject_once", "\x1b")
              ];

        return new PtyPendingPermission(
            RequestId: requestId,
            ToolName: toolName,
            Title: primaryTarget,
            Description: description,
            RawInputJson: null,
            DetectedAt: DateTimeOffset.UtcNow,
            Options: options);
    }

    private static bool IsChromeOrOptionLine(string line)
    {
        if (ContainsAnyIgnoreCase(line, ConfirmationQuestions) ||
            ContainsAnyIgnoreCase(line, MenuOptionIndicators))
        {
            return true;
        }

        var stripped = line.TrimStart('>', '❯', '●', '○', '*', '-', ' ');
        if (stripped.StartsWith("1.", StringComparison.Ordinal) ||
            stripped.StartsWith("2.", StringComparison.Ordinal) ||
            stripped.StartsWith("3.", StringComparison.Ordinal) ||
            stripped.StartsWith("1)", StringComparison.Ordinal) ||
            stripped.StartsWith("2)", StringComparison.Ordinal) ||
            stripped.StartsWith("3)", StringComparison.Ordinal) ||
            stripped.StartsWith("Yes", StringComparison.OrdinalIgnoreCase) ||
            stripped.StartsWith("No,", StringComparison.OrdinalIgnoreCase) ||
            stripped.StartsWith("Allow once", StringComparison.OrdinalIgnoreCase) ||
            stripped.StartsWith("Always allow", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("esc to cancel", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("esc to interrupt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsAnyIgnoreCase(string text, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static string StripAnsiAndBoxDrawing(string input)
    {
        var sb = new StringBuilder(input.Length);
        var i = 0;

        while (i < input.Length)
        {
            var ch = input[i];
            if (ch == '\x1b')
            {
                i++;
                if (i >= input.Length)
                {
                    break;
                }

                var next = input[i];
                if (next == '[')
                {
                    i++;
                    while (i < input.Length && input[i] >= 0x20 && input[i] <= 0x3F)
                    {
                        i++;
                    }

                    if (i < input.Length)
                    {
                        var finalByte = input[i];
                        i++;
                        if (finalByte == 'C')
                        {
                            sb.Append(' ');
                        }
                        else if (finalByte is 'B' or 'E' or 'H' or 'f')
                        {
                            sb.Append('\n');
                        }
                    }
                    continue;
                }

                if (next == ']')
                {
                    i++;
                    while (i < input.Length)
                    {
                        if (input[i] == '\x07')
                        {
                            i++;
                            break;
                        }
                        if (input[i] == '\x1b' && i + 1 < input.Length && input[i + 1] == '\\')
                        {
                            i += 2;
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                i++;
                continue;
            }

            if (ch == '\r')
            {
                if (i + 1 < input.Length && input[i + 1] == '\n')
                {
                    i++;
                }
                sb.Append('\n');
                i++;
                continue;
            }

            if (ch == '\n' || ch == '\t')
            {
                sb.Append(ch);
                i++;
                continue;
            }

            if (char.IsControl(ch))
            {
                i++;
                continue;
            }

            if (ch >= '\u2500' && ch <= '\u257F')
            {
                sb.Append(' ');
                i++;
                continue;
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    private static string CollapseWhitespace(string line)
    {
        var sb = new StringBuilder(line.Length);
        var inSpace = false;
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    inSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                inSpace = false;
            }
        }

        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }

        return sb.ToString();
    }
}
