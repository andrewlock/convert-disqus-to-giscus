using Spectre.Console;

internal static class Logger
{
    public static bool VerboseEnabled { get; set; }
    public static void Verbose(string markup)
    {
        if (VerboseEnabled)
        {
            AnsiConsole.MarkupLine(markup.EscapeMarkup());
        }
    }

    public static void Log(string markup)
    {
        AnsiConsole.MarkupLine(markup);
    }
    
}