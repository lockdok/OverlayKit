using System.Net;
using System.Net.Sockets;

namespace OverlayKit;

internal static class Program
{
    private static async Task Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("OverlayKit currently supports Windows only.");
            Environment.ExitCode = 1;
            return;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            await using var server = new OverlayServer();
            using var capture = new InputCapture(server.PublishFromHook);
            Console.WriteLine("OverlayKit is running.");
            Console.WriteLine($"Control panel: http://127.0.0.1:{server.Port}/");
            Console.WriteLine($"OBS browser source: http://127.0.0.1:{server.Port}/?mode=overlay");
            Console.WriteLine("Press Ctrl+C to stop.");
            await server.RunAsync(shutdown.Token);
        }
        catch (SocketException ex)
        {
            var port = Environment.GetEnvironmentVariable("OVERLAYKIT_PORT") ?? "17893";
            Console.Error.WriteLine($"Could not start the local web server: {ex.Message}");
            Console.Error.WriteLine($"Check whether port {port} is already in use.");
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"OverlayKit failed: {ex}");
            Environment.ExitCode = 1;
        }
    }
}
