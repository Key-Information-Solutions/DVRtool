using System.Text;

namespace DVRTool.Cli;

internal static class ConsolePrompt
{
    /// <summary>
    /// Reads a secret from the console with echo suppressed, so a site visit never needs
    /// the password on the command line (where it persists in shell history and audit logs).
    /// </summary>
    /// <exception cref="ArgumentException">Input is redirected, so nobody can type.</exception>
    internal static string ReadSecret(string prompt, string missingMessage)
    {
        if (Console.IsInputRedirected)
            throw new ArgumentException(missingMessage);

        Console.Error.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                    sb.Length--;
                continue;
            }
            if (key.KeyChar != '\0')
                sb.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }
}
