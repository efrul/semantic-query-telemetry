using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

// Taking the tool off this computer. The installer (installer\SearchDataSender.iss, Inno Setup) puts it in
// %LOCALAPPDATA%\Programs\SearchDataSender for this user, with an entry in Settings › Apps. Removing it there, or with
// the window's "הסרת הכלי מהמחשב", runs its uninstaller, which runs SearchDataSender.exe --uninstall before it removes
// the files: this asks whether to delete from the server what was sent, stops the background sender and takes it out
// of Windows' startup, and removes the tool's settings key and data folder. Nothing of another app is touched.
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

    // SearchDataSender.exe --uninstall [--quiet]. Quiet (a silent uninstall) asks nothing, and deletes nothing from the
    // server.
    public static int Run(bool quiet)
    {
        Remove(quiet, false);
        return 0;
    }

    // Returns false when deleting from the server failed: then the settings stay, with the sender id, to try again after
    // installing. [dataAfterExit]: the data folder once this process is gone (its window holds the font).
    public static bool Remove(bool quiet, bool dataAfterExit)
    {
        Startup.TurnOff();
        var settings = new Settings(new RegistryStore(Places.SettingsKey));
        bool keep = false;
        if (!quiet && SentFromHere(settings) &&
            Say("למחוק גם מהשרת ומהאתר את כל מה שנשלח מהמחשב הזה?\n\nבלי מחיקה, מה שנשלח נשאר במאגר החיפושים הפתוח.",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            var answer = new HttpService(settings.ServiceUrl).DeleteSender(settings.Sender);
            if (!answer.Ok)
            {
                keep = true;
                Say("המחיקה מהשרת לא הצליחה: " + answer.Message() + "\n\nההגדרות של הכלי נשארות במחשב, כדי שאם תתקין אותו שוב תוכל למחוק משם.",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        if (keep) return false;
        if (!dataAfterExit) Clean(Places.SettingsKey, Places.Data);
        else
        {
            Clean(Places.SettingsKey, null);
            CleanAfterExit();
        }
        return true;
    }

    // Something was sent with this computer's id: the window's figures, or the ledger
    static bool SentFromHere(Settings settings)
    {
        if (settings.SentPackages > 0) return true;
        try { return File.Exists(Places.Ledger) && new Ledger(Places.Ledger).SentCount > 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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

    static MessageBoxResult Say(string text, MessageBoxButton buttons, MessageBoxImage image)
    {
        return MessageBox.Show(text, AppInfo.Name, buttons, image, buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.OK,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
    }
}
