using System;
using System.Collections.Generic;

// The apps this tool reads on this computer, with the folders the user added.
public static class Readers
{
    // [zayitDatabasePath]: Zayit's database_path preference, when there is one (the app reads it from the registry).
    public static List<IReader> For(Settings settings, string zayitDatabasePath, Func<long> now)
    {
        var extra = settings.ExtraFolders;
        return new List<IReader>
        {
            ZayitReader.ForThisComputer(extra, zayitDatabasePath),
            OtzariaReader.ForThisComputer(extra, now),
        };
    }
}
