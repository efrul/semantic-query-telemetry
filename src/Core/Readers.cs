using System;
using System.Collections.Generic;

// The apps this tool reads on this computer, with the folders the user added.
public static class Readers
{
    // [zayitDatabasePath], [pardesDatabasePath]: each app's database_path preference, when it has one (the app layer
    // reads them from the registry).
    public static List<IReader> For(Settings settings, string zayitDatabasePath, string pardesDatabasePath, Func<long> now)
    {
        var extra = settings.ExtraFolders;
        return new List<IReader>
        {
            SeforimAppReader.ForZayit(extra, zayitDatabasePath),
            SeforimAppReader.ForPardes(extra, pardesDatabasePath),
            OtzariaReader.ForThisComputer(extra, now),
        };
    }
}
