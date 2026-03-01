// RUN
using mnmbox_tui.Services;
using mnmbox_tui.Views;
using RazorConsole.Core;

// Disable console beep by wrapping stdout
Console.SetOut(new NoBellTextWriter(Console.Out));

// Set console code page to UTF-8 for better character support
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch { }

await AppHost.RunAsync<SystemInfo>();
