using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

// The window: what was found on this computer, what would be sent (with the list to leave things out), sending now or
// saving to a file, sending in the background, the sender's id and deleting what was sent, and what went so far.
// Reading the apps and sending run off the window's thread; only one send at a time, whoever starts it (SendLock).
sealed class MainWindow : Window
{
    const int ListMax = 400;

    readonly Deps d;
    readonly StackPanel content = new StackPanel { Margin = new Thickness(18, 14, 18, 18) };

    List<SourceData> data;
    Selection sel;
    Ledger ledger;
    bool busy, showList, confirmDelete, confirmWithdraw, confirmRemove;
    string busyText, status;
    bool statusBad;
    long readAt;

    public MainWindow(Deps deps)
    {
        d = deps;
        ledger = d.OpenLedger();
        Theme.Dress(this);
        Title = AppInfo.Name;
        Icon = Theme.Image("app.png");
        var area = SystemParameters.WorkArea;
        Width = Math.Min(660, area.Width - 40);
        Height = Math.Min(820, area.Height - 30);
        MinWidth = 480;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(Theme.TitleBar(this, AppInfo.Name, Theme.Image("app.png")));

        // One island, as Zayit's home page: its wallpaper at the top, fading into the panel
        var island = new Grid { Margin = new Thickness(6, 0, 6, 6), Background = Theme.B("PANEL") };
        island.SizeChanged += (s, e) => island.Clip = new RectangleGeometry(new Rect(0, 0, island.ActualWidth, island.ActualHeight), 12, 12);
        var wall = Theme.Image(Theme.Dark ? "wallpaper_dark.jpg" : "wallpaper.jpg");
        if (wall != null)
        {
            var img = new Image { Source = wall, Stretch = Stretch.UniformToFill, Height = 300, VerticalAlignment = VerticalAlignment.Top, FlowDirection = FlowDirection.LeftToRight };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            island.Children.Add(img);
            var panel = Theme.B("PANEL").Color;
            island.Children.Add(new Rectangle
            {
                Height = 301,
                VerticalAlignment = VerticalAlignment.Top,
                Fill = new LinearGradientBrush(new GradientStopCollection {
                    // Light enough for the picture, strong enough for the words over it
                    new GradientStop(Color.FromArgb(150, panel.R, panel.G, panel.B), 0),
                    new GradientStop(Color.FromArgb(185, panel.R, panel.G, panel.B), 0.35),
                    new GradientStop(Color.FromArgb(225, panel.R, panel.G, panel.B), 0.7),
                    new GradientStop(panel, 1) }, 90)
            });
        }
        island.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content, Focusable = false });
        Grid.SetRow(island, 1);
        root.Children.Add(island);
        Content = root;

        var normal = island.Margin;
        StateChanged += (s, e) => island.Margin = WindowState == WindowState.Maximized
            ? new Thickness(normal.Left + 7, normal.Top, normal.Right + 7, normal.Bottom + 7) : normal;
        Loaded += (s, e) => StartRead();
        // Back to the window after searching in Zayit, Pardes or Otzaria: read again, at most every half minute
        Activated += (s, e) => { if (!busy && data != null && d.Now() - readAt > 30000) StartRead(); };
        Render();
    }

    // ---- Reading ----

    void StartRead()
    {
        if (busy) return;
        busy = true;
        busyText = "קורא את הנתונים של זית, פרדס ואוצריא…";
        Render();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var read = Engine.ReadAll(d.MakeReaders());
            Dispatcher.BeginInvoke(new Action(() =>
            {
                busy = false;
                ApplyRead(read);
            }));
        });
    }

    // The apps as just read: what would be sent now. Public for the self-test.
    public void ApplyRead(List<SourceData> read)
    {
        data = read;
        readAt = d.Now();
        ledger = d.OpenLedger();
        Reselect();
    }

    void Reselect()
    {
        sel = data == null ? null : Engine.Select(data, d.Settings, ledger);
        Render();
    }

    // ---- Sending, saving, deleting ----

    void StartSend()
    {
        if (busy || data == null) return;
        busy = true;
        busyText = "שולח…";
        status = null;
        confirmDelete = confirmWithdraw = confirmRemove = false;
        Render();
        var read = data;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            SendResult result = null;
            string blocked = null;
            using (var lk = SendLock.TryTake(10000))
            {
                if (lk == null) blocked = "השליחה ברקע רצה עכשיו. אפשר לנסות שוב בעוד רגע.";
                else
                {
                    // The background sender may have sent since this window read: start from the ledger as it is now
                    var fresh = d.OpenLedger();
                    var now = Engine.Select(read, d.Settings, fresh);
                    result = Engine.Send(now, d.Settings, fresh, d.MakeService(), d.Now, 1, 65000,
                        text => Dispatcher.BeginInvoke(new Action(() => { busyText = text; Render(); })));
                }
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                busy = false;
                if (blocked != null) { status = blocked; statusBad = true; Render(); }
                else ApplySendResult(result);
            }));
        });
    }

    // What a send did, said in the window. Public for the self-test.
    public void ApplySendResult(SendResult r)
    {
        var what = Summary(r.Searches, r.Grades, r.Marks);
        if (r.Complete)
        {
            status = (what.Length > 0 ? "נשלחו " + what + ". " : "") + "תודה!" +
                (r.RefusedRows > 0 ? " " + Fmt.Count(r.RefusedRows, "פריט אחד לא התקבל בשירות ולא יישלח שוב.", "פריטים לא התקבלו בשירות ולא יישלחו שוב.") : "");
            statusBad = false;
        }
        else
        {
            status = (what.Length > 0 ? "נשלחו " + what + ", והשאר לא: " : "") + r.StoppedBy.Message();
            statusBad = true;
        }
        ledger = d.OpenLedger();
        Reselect();
    }

    void SaveToFile()
    {
        if (busy || sel == null || sel.IsEmpty) return;
        var folder = FolderPicker.Pick(this, "התיקייה לשמירת הנתונים");
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            var parts = Engine.Build(sel, d.Settings, d.Now()).Parts(Limits.PartBytes);
            string first = null;
            for (int i = 0; i < parts.Count; i++)
            {
                var f = parts[i].WriteTo(folder, i, parts.Count);
                if (first == null) first = f;
            }
            status = parts.Count == 1 ? "נשמר בקובץ " + System.IO.Path.GetFileName(first) + ". אפשר להעלות אותו באתר המאגר."
                : "נשמרו " + parts.Count + " קבצים בתיקייה " + folder + ". אפשר להעלות אותם באתר המאגר.";
            statusBad = false;
        }
        catch (Exception e)
        {
            if (!(e is System.IO.IOException || e is UnauthorizedAccessException)) throw;
            status = "השמירה לא הצליחה: " + e.Message;
            statusBad = true;
        }
        Render();
    }

    void DeleteFromServer()
    {
        if (busy) return;
        busy = true;
        busyText = "מוחק מהשרת…";
        confirmDelete = false;
        Render();
        var sender = d.Settings.Sender;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var answer = d.MakeService().DeleteSender(sender);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                busy = false;
                if (answer.Ok)
                {
                    // A new id from now on. What was sent stays in the ledger, so it is not sent again
                    d.Settings.NewSender();
                    d.Settings.ResetSentStats();
                    status = "כל מה ששלחת נמחק מהשרת ומהאתר. מה שכבר נשלח לא יישלח שוב, ומה שתחפש מעכשיו יישלח עם מזהה חדש.";
                    statusBad = false;
                }
                else
                {
                    status = "המחיקה לא הצליחה: " + answer.Message();
                    statusBad = true;
                }
                Render();
            }));
        });
    }

    void Withdraw()
    {
        d.SetAutoSend(false);
        d.Settings.WithdrawConsent();
        MessageBox.Show(this, "ההסכמה בוטלה, והכלי לא ישלח יותר כלום. מה שכבר נשלח נשאר במאגר, אלא אם מחקת אותו.", AppInfo.Name,
            MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        Close();
    }

    // The uninstaller of Settings › Apps when the installer put the tool here, which asks itself; else, the bare exe,
    // the same removal from here after asking
    void RemoveTool()
    {
        if (!d.ManageStartup) return;
        var uninstaller = Uninstall.Uninstaller();
        if (uninstaller == null) { confirmRemove = true; Render(); return; }
        Program.LeaveWindow();
        Process.Start(new ProcessStartInfo(uninstaller) { UseShellExecute = true });
        Close();
    }

    void RemoveHere()
    {
        confirmRemove = false;
        if (!Uninstall.Remove(false, true)) { Render(); return; }
        MessageBox.Show(this, "הכלי הוסר מהמחשב. אפשר למחוק עכשיו גם את הקובץ שלו:\n" + Places.RunningExe, AppInfo.Name,
            MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        Close();
    }

    // Opens or closes the list of what would be sent. Public for the self-test.
    public void ToggleDetails()
    {
        showList = !showList;
        Render();
    }

    void AddFolder()
    {
        var folder = FolderPicker.Pick(this, "תיקייה של זית, של פרדס או של אוצריא (למשל עותק נייד)");
        if (string.IsNullOrEmpty(folder)) return;
        var list = d.Settings.ExtraFolders;
        if (!list.Exists(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))) list.Add(folder);
        d.Settings.ExtraFolders = list;
        StartRead();
    }

    // ---- Drawing ----

    public void Render()
    {
        content.Children.Clear();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 4) };
        var branch = Theme.Image("branch.png");
        if (branch != null) head.Children.Add(Theme.Picture(branch, 30, 30));
        var h = Theme.T(AppInfo.Name, 20, "TEXT", true);
        h.Margin = new Thickness(10, 0, 0, 0);
        h.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(h);
        content.Children.Add(head);
        var sub = Theme.T("החיפושים שלך בזית, בפרדס ובאוצריא, למאגר החיפושים הפתוח, כדי לשפר את החיפוש בספרייה התורנית.", 13, "SUB");
        sub.Margin = new Thickness(2, 0, 0, 14);
        content.Children.Add(sub);

        if (data == null)
        {
            content.Children.Add(Theme.Card(Theme.T(busy ? busyText : "קורא…", 13.5, "SUB")));
            return;
        }
        content.Children.Add(FoundCard());
        content.Children.Add(WhatCard());
        content.Children.Add(SendCard());
        content.Children.Add(PrivacyCard());
        content.Children.Add(StatsCard());
    }

    UIElement FoundCard()
    {
        var rows = new List<UIElement> { Theme.Title("מה נמצא במחשב") };
        foreach (var src in data)
        {
            var s = src;
            var line = new DockPanel { Margin = new Thickness(0, 4, 0, 2), LastChildFill = false };
            var cb = new CheckBox { Content = Sources.Name(s.Source), IsChecked = s.Found && d.Settings.IsSourceOn(s.Source), IsEnabled = s.Found && !busy };
            cb.Checked += (o, e) => { d.Settings.SetSourceOn(s.Source, true); Reselect(); };
            cb.Unchecked += (o, e) => { d.Settings.SetSourceOn(s.Source, false); Reselect(); };
            line.Children.Add(cb);
            var pill = s.Found ? Theme.Pill("נמצא", "GREEN", "GREENSOFT") : Theme.Pill("לא נמצא", "SUB", "BORDER");
            pill.Margin = new Thickness(10, 0, 0, 0);
            line.Children.Add(pill);
            rows.Add(line);
            foreach (var p in s.Places)
            {
                var path = Theme.PathText(p, 12, "SUB", 70);
                path.Margin = new Thickness(26, 0, 0, 2);
                rows.Add(path);
            }
            foreach (var problem in s.Problems) rows.Add(Theme.Note(Theme.G_WARN, problem, "WARN", "WARNSOFT"));
        }
        var add = Theme.Btn(this, "הוספת תיקייה…", "Link", AddFolder);
        add.IsEnabled = !busy;
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 8, 0, 0);
        add.ToolTip = "עותק נייד של זית, של פרדס או של אוצריא, או תיקיית נתונים שהועברה";
        rows.Add(add);
        return Theme.Card(rows.ToArray());
    }

    UIElement WhatCard()
    {
        var rows = new List<UIElement> { Theme.Title("מה יישלח") };
        bool anyGrades = data.Exists(x => x.Found && x.Grades.Count > 0);
        bool anyMarks = data.Exists(x => x.Found && x.Marks.Count > 0);
        rows.Add(KindRow("חיפושים", sel.Searches.Count, d.Settings.SearchesOn, v => d.Settings.SearchesOn = v));
        if (anyGrades) rows.Add(KindRow("דירוגים לתוצאות", sel.Grades.Count, d.Settings.GradesOn, v => d.Settings.GradesOn = v));
        if (anyMarks) rows.Add(KindRow("סימונים בצורת הדף", sel.Marks.Count, d.Settings.DafOn, v => d.Settings.DafOn = v));

        var summary = sel.IsEmpty ? "אין כרגע שום דבר חדש לשלוח." : "חדש מאז השליחה הקודמת: " + Summary(sel.Searches.Count, sel.Grades.Count, sel.Marks.Count) + ".";
        var left = sel.ExcludedSearches.Count + sel.ExcludedGrades.Count;
        if (left > 0) summary += " " + Fmt.Count(left, "פריט אחד הורדת מהשליחה.", "פריטים הורדת מהשליחה.");
        var st = Theme.T(summary, 13, "SUB");
        st.Margin = new Thickness(0, 8, 0, 0);
        rows.Add(st);

        if (!sel.IsEmpty || left > 0)
        {
            var toggle = Theme.Btn(this, showList ? "הסתרת הפרטים" : "עיון בפרטים והורדת פריטים מהשליחה", "Link", ToggleDetails);
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            toggle.Margin = new Thickness(0, 6, 0, 0);
            rows.Add(toggle);
        }
        if (showList && (!sel.IsEmpty || left > 0)) rows.Add(DetailList());
        return Theme.Card(rows.ToArray());
    }

    UIElement KindRow(string name, int count, bool on, Action<bool> set)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        var cb = new CheckBox { Content = name, IsChecked = on, IsEnabled = !busy };
        cb.Checked += (o, e) => { set(true); Reselect(); };
        cb.Unchecked += (o, e) => { set(false); Reselect(); };
        line.Children.Add(cb);
        if (on)
        {
            var pill = Theme.Pill(count == 0 ? "אין חדש" : Fmt.Num(count) + " חדשים");
            pill.Margin = new Thickness(10, 0, 0, 0);
            line.Children.Add(pill);
        }
        return line;
    }

    UIElement DetailList()
    {
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        int shown = 0;
        if (sel.ExcludedSearches.Count + sel.ExcludedGrades.Count > 0)
        {
            var back = Theme.Btn(this, "להחזיר הכל לשליחה", "Link", () =>
            {
                foreach (var s in sel.ExcludedSearches) ledger.SetExcluded(s.Key, false);
                foreach (var g in sel.ExcludedGrades) ledger.SetExcluded(g.Key, false);
                Reselect();
            });
            back.IsEnabled = !busy;
            back.HorizontalAlignment = HorizontalAlignment.Left;
            back.Margin = new Thickness(0, 0, 0, 6);
            list.Children.Add(back);
        }

        foreach (var s in Merge(sel.Searches, sel.ExcludedSearches))
        {
            if (shown++ >= ListMax) break;
            var row = s;
            var when = row.TimeUnknown ? "בלי תאריך" : Fmt.When(row.LastAt);
            var detail = Sources.Name(row.Source) + ", " + row.ScopeLabel() + ", " + Fmt.Count(row.Count, "פעם אחת", "פעמים") + ", " + when;
            list.Children.Add(Item(row.Query, detail, !ledger.IsExcluded(row.Key), v => ledger.SetExcluded(row.Key, !v)));
        }
        foreach (var g in Merge(sel.Grades, sel.ExcludedGrades))
        {
            if (shown++ >= ListMax) break;
            var row = g;
            var detail = Sources.Name(row.Source) + ", דירוג: " + row.GradeLabel() + ", " + row.ResultLabel() + ", " + Fmt.When(row.GradedAt);
            list.Children.Add(Item(row.Query, detail, !ledger.IsExcluded(row.Key), v => ledger.SetExcluded(row.Key, !v)));
        }
        int total = sel.Searches.Count + sel.ExcludedSearches.Count + sel.Grades.Count + sel.ExcludedGrades.Count;
        if (total > ListMax)
            list.Children.Add(Theme.T("מוצגים " + Fmt.Num(ListMax) + " מתוך " + Fmt.Num(total) + ". הכל יישלח, חוץ ממה שהורדת.", 12, "SUB"));
        return new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
    }

    static List<T> Merge<T>(List<T> a, List<T> b)
    {
        var all = new List<T>(a);
        all.AddRange(b);
        return all;
    }

    UIElement Item(string words, string detail, bool included, Action<bool> set)
    {
        var text = new StackPanel();
        var w = Theme.T(words, 13.5, included ? "TEXT" : "SUB", true);
        w.TextTrimming = TextTrimming.CharacterEllipsis;
        w.TextWrapping = TextWrapping.NoWrap;
        w.ToolTip = words;
        text.Children.Add(w);
        text.Children.Add(Theme.T(detail, 11.5, "SUB"));
        var cb = new CheckBox { Content = text, IsChecked = included, IsEnabled = !busy, Margin = new Thickness(0, 3, 0, 3) };
        System.Windows.Automation.AutomationProperties.SetName(cb, words);
        cb.Checked += (o, e) => { set(true); Reselect(); };
        cb.Unchecked += (o, e) => { set(false); Reselect(); };
        return cb;
    }

    UIElement SendCard()
    {
        var rows = new List<UIElement> { Theme.Title("שליחה") };
        rows.Add(Theme.Note(Theme.G_INFO, "מה שנשלח מתפרסם לכולם באתר מאגר החיפושים הפתוח, בלי שם ובלי שום פרט על המחשב.", "TEXT", "ACCENTSOFT"));
        var site = Theme.Btn(this, "לאתר המאגר", "Link", ConsentWindow.OpenSite);
        site.HorizontalAlignment = HorizontalAlignment.Left;
        rows.Add(site);

        var send = Theme.IconBtn(this, Theme.G_SEND, busy && busyText != null && busyText.StartsWith("שולח") ? busyText : "שליחה עכשיו", "Default", StartSend);
        send.IsEnabled = !busy && !sel.IsEmpty;
        var save = Theme.IconBtn(this, Theme.G_SAVE, "שמירה לקובץ…", "Outlined", SaveToFile);
        save.IsEnabled = !busy && !sel.IsEmpty;
        save.ToolTip = "קובץ שאפשר להעלות אחר כך באתר המאגר, למשל ממחשב אחר";
        rows.Add(Theme.Row(send, save));
        if (busy && busyText != null)
        {
            var b = Theme.T(busyText, 12.5, "SUB");
            b.Margin = new Thickness(0, 8, 0, 0);
            rows.Add(b);
        }
        if (status != null)
            rows.Add(statusBad ? Theme.Note(Theme.G_WARN, status, "RED", "REDSOFT") : Theme.Note(Theme.G_OK, status, "GREEN", "GREENSOFT"));

        var auto = new CheckBox
        {
            Content = "לשלוח לבד ברקע: בהפעלת המחשב, בכל חיבור לרשת ופעם בשעה",
            IsChecked = d.Settings.AutoSend,
            IsEnabled = !busy,
            Margin = new Thickness(0, 12, 0, 0)
        };
        auto.Checked += (o, e) =>
        {
            var error = d.SetAutoSend(true);
            if (error != null) { status = error; statusBad = true; }
            Render();
        };
        auto.Unchecked += (o, e) => { d.SetAutoSend(false); Render(); };
        rows.Add(auto);
        return Theme.Card(rows.ToArray());
    }

    UIElement PrivacyCard()
    {
        var rows = new List<UIElement> { Theme.Title("המזהה שלך ומחיקה") };
        var sender = d.Settings.Sender;
        var who = Theme.T("השליחות מהמחשב הזה מסומנות במספר אקראי, שלא מוצג באתר: " + sender.Substring(0, 8), 12.5, "SUB");
        who.ToolTip = sender;
        rows.Add(who);
        if (confirmDelete)
        {
            rows.Add(Theme.Note(Theme.G_WARN, "למחוק מהשרת ומהאתר את כל מה שנשלח מהמחשב הזה? אי אפשר לבטל את זה.", "RED", "REDSOFT"));
            rows.Add(Theme.Row(Theme.Btn(this, "מחיקה", "Danger", DeleteFromServer), Theme.Btn(this, "ביטול", "Outlined", () => { confirmDelete = false; Render(); })));
        }
        else if (confirmWithdraw)
        {
            rows.Add(Theme.Note(Theme.G_WARN, "לבטל את ההסכמה? הכלי לא ישלח יותר כלום. מה שכבר נשלח נשאר במאגר, אלא אם תמחק אותו קודם.", "WARN", "WARNSOFT"));
            rows.Add(Theme.Row(Theme.Btn(this, "ביטול ההסכמה", "Danger", Withdraw), Theme.Btn(this, "חזרה", "Outlined", () => { confirmWithdraw = false; Render(); })));
        }
        else if (confirmRemove)
        {
            rows.Add(Theme.Note(Theme.G_WARN, "להסיר את הכלי מהמחשב? השליחה ברקע תיפסק, וההגדרות שלו ורשימת מה שנשלח יימחקו. את הקובץ עצמו אפשר למחוק אחר כך.", "WARN", "WARNSOFT"));
            rows.Add(Theme.Row(Theme.Btn(this, "הסרה", "Danger", RemoveHere), Theme.Btn(this, "חזרה", "Outlined", () => { confirmRemove = false; Render(); })));
        }
        else
        {
            var del = Theme.Btn(this, "מחיקת כל מה ששלחתי", "Link", () => { confirmDelete = true; Render(); });
            del.IsEnabled = !busy;
            var withdraw = Theme.Btn(this, "ביטול ההסכמה", "Link", () => { confirmWithdraw = true; Render(); });
            withdraw.IsEnabled = !busy;
            var remove = Theme.Btn(this, "הסרת הכלי מהמחשב", "Link", RemoveTool);
            remove.IsEnabled = !busy;
            rows.Add(Theme.Row(del, withdraw));
            rows.Add(Theme.Row(remove));
        }
        return Theme.Card(rows.ToArray());
    }

    UIElement StatsCard()
    {
        var s = d.Settings;
        var rows = new List<UIElement> { Theme.Title("נשלח מהמחשב הזה") };
        if (s.SentPackages == 0)
        {
            rows.Add(Theme.T("עוד לא נשלח כלום.", 13, "SUB"));
            return Theme.Card(rows.ToArray());
        }
        rows.Add(Theme.Pair("חיפושים", Fmt.Num(s.SentSearches)));
        if (s.SentGrades > 0) rows.Add(Theme.Pair("דירוגים", Fmt.Num(s.SentGrades)));
        if (s.SentMarks > 0) rows.Add(Theme.Pair("סימונים בצורת הדף", Fmt.Num(s.SentMarks)));
        rows.Add(Theme.Pair("חבילות", Fmt.Num(s.SentPackages)));
        rows.Add(Theme.Pair("לראשונה", Fmt.When(s.FirstSentAt)));
        rows.Add(Theme.Pair("לאחרונה", Fmt.When(s.LastSentAt)));
        return Theme.Card(rows.ToArray());
    }

    static string Summary(int searches, int grades, int marks)
    {
        var parts = new List<string>();
        if (searches > 0) parts.Add(Fmt.Count(searches, "חיפוש אחד", "חיפושים"));
        if (grades > 0) parts.Add(Fmt.Count(grades, "דירוג אחד", "דירוגים"));
        if (marks > 0) parts.Add(Fmt.Count(marks, "סימון אחד", "סימונים"));
        if (parts.Count == 0) return "";
        if (parts.Count == 1) return parts[0];
        return string.Join(", ", parts.GetRange(0, parts.Count - 1).ToArray()) + " ו־" + parts[parts.Count - 1];
    }
}
