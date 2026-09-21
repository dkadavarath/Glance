namespace Glance.Services;

// Unpackaged builds have no package identity, so Windows.Storage.ApplicationData.Current
// throws. The WinAppSDK equivalent takes an explicit identity and works either way.
internal static class AppData
{
    private static readonly Microsoft.Windows.Storage.ApplicationData _current =
        Microsoft.Windows.Storage.ApplicationData.GetForUnpackaged("Glance", "Glance");

    // Unlike the packaged LocalFolder this replaces, GetForUnpackaged resolves a path
    // without creating it, so every write through it would fail until we create it.
    public static string LocalPath { get; } = CreateLocalPath();

    public static Microsoft.Windows.Storage.ApplicationDataContainer LocalSettings => _current.LocalSettings;

    private static string CreateLocalPath()
    {
        string path = _current.LocalPath;
        Directory.CreateDirectory(path);
        return path;
    }
}
