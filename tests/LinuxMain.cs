using System;
using System.IO;

// The core's tests on Linux (tools/test-core.sh): the same tests SearchDataSender.exe --selftest runs on Windows.
static class LinuxMain
{
    static int Main(string[] args)
    {
        var dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "search-data-sender-tests");
        return CoreTests.Run(dir, Console.WriteLine) == 0 ? 0 : 1;
    }
}
