using System.Text;

namespace Acplite.Client;

/// <summary>
/// Sanitizes untrusted strings (session titles, paths, agent output, error messages) by stripping
/// ANSI escape sequences and control characters before terminal rendering.
/// </summary>
internal static class TerminalText
{
    public static string Clean(string? input, int maxLength = 4096)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(input.Length, maxLength));
        var i = 0;
        while (i < input.Length && sb.Length < maxLength)
        {
            var ch = input[i];

            if (ch == '\x1b')
            {
                i++;
                if (i < input.Length && input[i] == '[')
                {
                    i++;
                    while (i < input.Length && (input[i] < 0x40 || input[i] > 0x7E))
                    {
                        i++;
                    }
                    if (i < input.Length) i++;
                }
                else if (i < input.Length && input[i] == ']')
                {
                    i++;
                    while (i < input.Length && input[i] != '\x07' && !(input[i] == '\x1b' && i + 1 < input.Length && input[i + 1] == '\\'))
                    {
                        i++;
                    }
                    if (i < input.Length)
                    {
                        i += input[i] == '\x07' ? 1 : 2;
                    }
                }
                continue;
            }

            if (ch is '\n' or '\t' || !char.IsControl(ch))
            {
                sb.Append(ch);
            }

            i++;
        }

        return sb.ToString();
    }
}
