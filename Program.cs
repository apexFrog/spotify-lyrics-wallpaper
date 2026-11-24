namespace SpotifyLyricsWallpaper;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    private static void Main()
    {
        _singleInstanceMutex = new Mutex(true, "SpotifyLyricsWallpaper_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
