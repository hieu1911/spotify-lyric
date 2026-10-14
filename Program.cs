using System;
using System.Threading;

namespace SpotifyLyric;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "SpotifyLyric_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // "--wait": relaunched by a closing instance, wait for it (and for Explorer) to come back.
            if (Array.IndexOf(args, "--wait") < 0) return;
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) return;
            }
            catch (AbandonedMutexException) { }
        }
        if (Array.IndexOf(args, "--wait") >= 0) Thread.Sleep(3000);

        var app = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
        };
        _ = new LyricController(Settings.Load());
        app.Run();
    }
}
