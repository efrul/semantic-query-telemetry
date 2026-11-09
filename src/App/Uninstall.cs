using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

// Taking the tool off this computer. The installer (installer\SearchDataSender.iss, Inno Setup) puts it in
// %LOCALAPPDATA%\Programs\SearchDataSender for this user, with an entry in Settings › Apps. Removing it there, or with
// the window's "הסרת הכלי מהמחשב", runs its uninstaller, which runs SearchDataSender.exe --uninstall before it removes
// the files: this stops the background sender and takes it out of Windows' startup, and removes the tool's settings key
// and data folder. Nothing of another app is touched, and nothing on the server.
static class Uninstall
{
    // The installer's AppId: its entry in Settings › Apps is HKCU\...\Uninstall\<AppId>_is1
    public const string AppId = "{623648DC-7C9D-4C51-8DA7-127FFFC75A23}";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";

    // The installed uninstaller (unins000.exe), or null when the installer did not put the tool here
    public static string Uninstaller()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                var s = k == null ? null : k.GetValue("UninstallString") as string;
                if (string.IsNullOrEmpty(s)) return null;
                s = s.Trim().Trim('"');
                return File.Exists(s) ? s : null;
            }
        }
        catch (System.Security.SecurityException) { return null; }
        catch (IOException) { return null; }
    }

    // SearchDataSender.exe --uninstall
    public static int Run()
    {
        Remove(false);
        return 0;
    }

    // [dataAfterExit]: the data folder once this process is gone (its window holds the font). What was sent stays on the
    // server: the window's "מחיקת כל מה ששלחתי" deletes it.
    public static void Remove(bool dataAfterExit)
    {
        Startup.TurnOff();
        if (!dataAfterExit) Clean(Places.SettingsKey, Places.Data);
        else
        {
            Clean(Places.SettingsKey, null);
            CleanAfterExit();
        }
    }

    // Removes the tool's settings key and data folder (the ledger, the unpacked font). Public for the self-test.
    public static void Clean(string settingsKey, string dataDir)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(settingsKey, false); }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
        for (int attempt = 0; attempt < 10 && dataDir != null && Directory.Exists(dataDir); attempt++)
        {
            try { Directory.Delete(dataDir, true); }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }

    // The data folder, a few seconds after this process exits
    static void CleanAfterExit()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var args = "/d /c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"" + Places.Data + "\"";
        try { Process.Start(new ProcessStartInfo(cmd, args) { UseShellExecute = false, CreateNoWindow = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
