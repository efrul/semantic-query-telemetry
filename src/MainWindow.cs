using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.IO.Path;

class MainWindow : Window
{
    readonly StackPanel content = new StackPanel { Margin = new Thickness(18, 14, 18, 18) };
    readonly ScrollViewer scroll;

    ScanResult scan;
    bool zayitOn;
    bool otzariaOn;
    bool searchesOn;
    bool gradesOn;
    bool dafOn;
    bool allFromStart;
    bool autoSend;
    bool reviewOpen;
    bool gradesReviewOpen;
    bool confirmDeleteServer;
    bool editingUrl;
    string urlDraft;
    readonly HashSet<string> excludedKeys = new HashSet<string>(StringComparer.Ordinal);
    readonly HashSet<string> excludedGradeKeys = new HashSet<string>(StringComparer.Ordinal);

    bool busy;
    string busyLabel;
    string statusText;
    bool statusError;
    string lastSavedFile;

    public MainWindow()
    {
        Theme.Dress(this);
        Title = App.Name;
        Icon = Theme.Image("app.png");
        var area = SystemParameters.WorkArea;
        Width = Math.Min(640, area.Width - 40);
        Height = Math.Min(780, area.Height - 30);
        MinWidth = 480;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        zayitOn = Prefs.IsSourceEnabled("zayit", true);
        otzariaOn = Prefs.IsSourceEnabled("otzaria", true);
        searchesOn = Prefs.IsKindEnabled("searches", true);
        gradesOn = Prefs.IsKindEnabled("grades", true);
        dafOn = Prefs.IsKindEnabled("daf", true);
        autoSend = Prefs.IsAutoSendEnabled();
        urlDraft = Service.ResolveUrl();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(Theme.TitleBar(this, App.Name, Theme.Image("app.png")));

        // One island, as Zayit's home page: its olive wallpaper at the top, fading into the panel
        var island = new Grid { Margin = new Thickness(6, 0, 6, 6), Background = Theme.B("PANEL") };
        island.SizeChanged += (s, e) => island.Clip = new RectangleGeometry(new Rect(0, 0, island.ActualWidth, island.ActualHeight), 12, 12);
        var wall = Theme.Image(Theme.Dark ? "wallpaper_dark.jpg" : "wallpaper.jpg");
        if (wall != null)
        {
            var img = new System.Windows.Controls.Image
            {
                Source = wall, Stretch = Stretch.UniformToFill, Height = 330,
                VerticalAlignment = VerticalAlignment.Top, FlowDirection = FlowDirection.LeftToRight
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            island.Children.Add(img);
            var panel = Theme.B("PANEL").Color;
            island.Children.Add(new Rectangle
            {
                Height = 331, VerticalAlignment = VerticalAlignment.Top,
                Fill = new LinearGradientBrush(new GradientStopCollection {
                    new GradientStop(Color.FromArgb(51, panel.R, panel.G, panel.B), 0),
                    new GradientStop(Color.FromArgb(102, panel.R, panel.G, panel.B), 0.35),
                    new GradientStop(Color.FromArgb(204, panel.R, panel.G, panel.B), 0.7),
                    new GradientStop(panel, 1) }, 90)
            });
        }
        scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content, Focusable = false };
        island.Children.Add(scroll);
        Grid.SetRow(island, 1);
        root.Children.Add(island);
        Content = root;

        var normal = island.Margin;
        StateChanged += (s, e) => island.Margin = WindowState == WindowState.Maximized
            ? new Thickness(normal.Left + 7, normal.Top, normal.Right + 7, normal.Bottom + 7)
            : normal;

        Activated += (s, e) => { if (!busy) RescanAndRender(); };
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        Closed += (s, e) =>
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        };

        RescanAndRender();
    }

    void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) TriggerNetworkSend();
    }

    void OnNetworkAddressChanged(object sender, EventArgs e)
    {
        TriggerNetworkSend();
    }

    void TriggerNetworkSend()
    {
        if (busy || !autoSend) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(2500);
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable() || busy || !autoSend) return;
                if (Service.RunAutoOnce())
                    Dispatcher.BeginInvoke(new Action(RescanAndRender));
            }
            catch { }
        });
    }

    public void RescanAndRender()
    {
        scan = Sources.Scan(allFromStart);
        Render();
    }

    List<SearchRow> ActiveSearches()
    {
        var list = new List<SearchRow>();
        if (scan == null) return list;
        if (zayitOn && scan.Zayit.Found) list.AddRange(scan.Zayit.Searches);
        if (otzariaOn && scan.Otzaria.Found) list.AddRange(scan.Otzaria.Searches);
        return list;
    }

    List<GradeRow> ActiveGrades()
    {
        var list = new List<GradeRow>();
        if (scan == null) return list;
        if (zayitOn && scan.Zayit.Found) list.AddRange(scan.Zayit.Grades);
        if (otzariaOn && scan.Otzaria.Found) list.AddRange(scan.Otzaria.Grades);
        return list;
    }

    List<DafReview> ActiveDaf()
    {
        var list = new List<DafReview>();
        if (scan == null) return list;
        if (zayitOn && scan.Zayit.Found) list.AddRange(scan.Zayit.Daf);
        return list;
    }

    public void Render()
    {
        content.Children.Clear();

        // Header
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 4) };
        var branch = Theme.Image("branch.png");
        if (branch != null) head.Children.Add(Theme.Picture(branch, 30, 30));
        var h = Theme.T(App.Name, 20, "TEXT", true);
        h.Margin = new Thickness(10, 0, 0, 0);
        h.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(h);
        content.Children.Add(head);

        var sub = Theme.T("שליחת היסטוריית החיפושים, דירוגי התוצאות וסימוני צורת הדף מזית ומאוצריא לשיפור החיפוש הסמנטי של פרדס.", 13, "SUB");
        sub.Margin = new Thickness(2, 0, 0, 14);
        content.Children.Add(sub);

        if (statusText != null)
        {
            content.Children.Add(Theme.Note(
                statusError ? Theme.G_WARN : Theme.G_OK,
                statusText,
                statusError ? "RED" : "TEXT",
                statusError ? "REDSOFT" : "ACCENTSOFT"));
        }

        content.Children.Add(SourcesCard());
        content.Children.Add(WhatToSendCard());
        content.Children.Add(SentStatsCard());
        content.Children.Add(ActionsAndPrivacyCard());

        var credit = Theme.T("Powered by the technologies that drive Zayit — https://zayitapp.com/", 11.5, "SUB");
        credit.FlowDirection = FlowDirection.LeftToRight;
        credit.TextAlignment = TextAlignment.Center;
        credit.Margin = new Thickness(0, 6, 0, 0);
        content.Children.Add(credit);
    }

    // ---- Card 1: Detected Installations (Zayit / Otzaria) ----

    UIElement SourcesCard()
    {
        var rows = new List<UIElement> { Theme.Title("תוכנות שזוהו במחשב") };

        rows.Add(SourceRow(scan.Zayit, zayitOn, true, val =>
        {
            zayitOn = val;
            Prefs.SetSourceEnabled("zayit", val);
            Render();
        }));

        var sep = new Border { Height = 1, Background = Theme.B("BORDER"), Margin = new Thickness(0, 10, 0, 10) };
        rows.Add(sep);

        rows.Add(SourceRow(scan.Otzaria, otzariaOn, false, val =>
        {
            otzariaOn = val;
            Prefs.SetSourceEnabled("otzaria", val);
            Render();
        }));

        var addFolder = Theme.IconBtn(this, Theme.G_FOLDER, "הוספת תיקייה…", "Outlined", () =>
        {
            var dir = FolderPicker.Pick(this, "בחירת תיקיית נתונים או ספרייה של זית או אוצריא");
            if (string.IsNullOrEmpty(dir)) return;
            if (!Sources.ExtraFolders.Contains(dir)) Sources.ExtraFolders.Add(dir);
            statusText = null;
            RescanAndRender();
        });
        var refresh = Theme.IconBtn(this, Theme.G_REFRESH, "סריקה מחדש", "Outlined", () =>
        {
            statusText = null;
            RescanAndRender();
        });
        rows.Add(Theme.Row(addFolder, refresh));

        return Theme.Card(rows.ToArray());
    }

    UIElement SourceRow(SourceSnapshot s, bool isChecked, bool hasDaf, Action<bool> onToggle)
    {
        var sp = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cb = new CheckBox
        {
            Content = Theme.T(s.Title, 14, "TEXT", true),
            IsChecked = isChecked && s.Found,
            IsEnabled = s.Found && !busy
        };
        cb.Checked += (sender, e) => onToggle(true);
        cb.Unchecked += (sender, e) => onToggle(false);
        top.Children.Add(cb);

        string badgeText;
        if (!s.Found)
            badgeText = "לא נמצא במחשב";
        else
        {
            var parts = new List<string>();
            parts.Add(Fmt.Num(s.Searches.Count) + " חיפושים");
            parts.Add(Fmt.Num(s.Grades.Count) + " דירוגים");
            if (hasDaf && s.DafMarkCount > 0) parts.Add(Fmt.Num(s.DafMarkCount) + " בצורת הדף");
            if (!string.IsNullOrEmpty(s.LibraryVersion)) parts.Add("מהדורה " + s.LibraryVersion);
            badgeText = string.Join(" · ", parts.ToArray());
        }
        var pill = Theme.Pill(badgeText, s.Found ? "ACCENT" : "SUB", s.Found ? "ACCENTSOFT" : "BORDER");
        Grid.SetColumn(pill, 1);
        top.Children.Add(pill);
        sp.Children.Add(top);

        if (s.Found && !string.IsNullOrEmpty(s.PathFound))
        {
            var p = Theme.PathText(s.PathFound, 12, "SUB", 68);
            p.Margin = new Thickness(26, 2, 0, 0);
            sp.Children.Add(p);
        }
        return sp;
    }

    // ---- Card 2: What to Send + Query Review ----

    UIElement WhatToSendCard()
    {
        var rows = new List<UIElement> { Theme.Title("מה לשלוח") };

        var allSearches = ActiveSearches();
        int includedSearches = allSearches.Count(s => !excludedKeys.Contains(s.Key));
        var grades = ActiveGrades();
        int includedGrades = grades.Count(g => !excludedGradeKeys.Contains(g.Key));
        var daf = ActiveDaf();
        int dafMarks = daf.Sum(r => r.Marks.Count);

        // 1. Searches checkbox row
        rows.Add(KindRow(
            "היסטוריית החיפושים (המילים שחיפשת ואיפה)",
            allSearches.Count == 0
                ? "אין חדש"
                : (includedSearches == allSearches.Count
                    ? Fmt.Num(allSearches.Count) + " חיפושים"
                    : Fmt.Num(includedSearches) + " מתוך " + Fmt.Num(allSearches.Count)),
            searchesOn,
            allSearches.Count > 0,
            val => { searchesOn = val; Prefs.SetKindEnabled("searches", val); Render(); }));

        // Expandable query review list
        if (searchesOn && allSearches.Count > 0)
            rows.Add(QueryReviewSection(allSearches, includedSearches));

        // 2. Grades checkbox row
        rows.Add(KindRow(
            "הציונים שניתנו לתוצאות החיפוש",
            grades.Count == 0
                ? "אין חדש"
                : (includedGrades == grades.Count
                    ? Fmt.Num(grades.Count) + " דירוגים"
                    : Fmt.Num(includedGrades) + " מתוך " + Fmt.Num(grades.Count)),
            gradesOn,
            grades.Count > 0,
            val => { gradesOn = val; Prefs.SetKindEnabled("grades", val); Render(); }));

        // Expandable grades review list
        if (gradesOn && grades.Count > 0)
            rows.Add(GradesReviewSection(grades, includedGrades));

        // 3. Daf marks checkbox row
        rows.Add(KindRow(
            "השורות שסומנו בצורת הדף",
            dafMarks == 0 ? "אין חדש" : Fmt.Num(dafMarks) + " שורות ב־" + Fmt.Num(daf.Count) + " מסכתות",
            dafOn,
            dafMarks > 0,
            val => { dafOn = val; Prefs.SetKindEnabled("daf", val); Render(); }));

        // Incremental vs All from start
        long lastSent = scan.LastSentAt;
        if (lastSent > 0 || allFromStart)
        {
            var sep = new Border { Height = 1, Background = Theme.B("BORDER"), Margin = new Thickness(0, 10, 0, 8) };
            rows.Add(sep);
            if (lastSent > 0)
                rows.Add(Theme.Pair("שליחה אחרונה", Fmt.When(lastSent)));
            var cbAll = new CheckBox
            {
                Content = "הכל מההתחלה, לא רק מה שחדש מאז הפעם הקודמת",
                IsChecked = allFromStart,
                IsEnabled = !busy,
                Margin = new Thickness(0, 6, 0, 0)
            };
            cbAll.Checked += (s, e) => { allFromStart = true; RescanAndRender(); };
            cbAll.Unchecked += (s, e) => { allFromStart = false; RescanAndRender(); };
            rows.Add(cbAll);
        }

        return Theme.Card(rows.ToArray());
    }

    UIElement KindRow(string label, string countText, bool isChecked, bool hasItems, Action<bool> onToggle)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cb = new CheckBox
        {
            Content = label,
            IsChecked = isChecked,
            IsEnabled = !busy
        };
        cb.Checked += (s, e) => onToggle(true);
        cb.Unchecked += (s, e) => onToggle(false);
        g.Children.Add(cb);

        var pill = Theme.Pill(countText, hasItems && isChecked ? "ACCENT" : "SUB", hasItems && isChecked ? "ACCENTSOFT" : "BORDER");
        Grid.SetColumn(pill, 1);
        g.Children.Add(pill);
        return g;
    }

    UIElement QueryReviewSection(List<SearchRow> allSearches, int includedCount)
    {
        var wrap = new StackPanel { Margin = new Thickness(26, 2, 0, 8) };
        var toggleText = (reviewOpen ? "▾ " : "◂ ") + "עיון בחיפושים שיישלחו (" + Fmt.Num(includedCount) + " מתוך " + Fmt.Num(allSearches.Count) + ")";
        var toggleBtn = Theme.Btn(this, toggleText, "Link", () => { reviewOpen = !reviewOpen; Render(); });
        toggleBtn.HorizontalAlignment = HorizontalAlignment.Left;
        wrap.Children.Add(toggleBtn);

        if (!reviewOpen) return wrap;

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        tools.Children.Add(Theme.Btn(this, "בחר הכל", "Link", () => { excludedKeys.Clear(); Render(); }));
        var clearBtn = Theme.Btn(this, "נקה הכל", "Link", () =>
        {
            foreach (var s in allSearches) excludedKeys.Add(s.Key);
            Render();
        });
        clearBtn.Margin = new Thickness(14, 0, 0, 0);
        tools.Children.Add(clearBtn);
        wrap.Children.Add(tools);

        var listStack = new StackPanel();
        // Show newest first in the review list
        foreach (var item in allSearches.AsEnumerable().Reverse())
        {
            var s = item;
            bool checkedItem = !excludedKeys.Contains(s.Key);
            var rowGrid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition());
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cb = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = s.Query,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = s.Query,
                    MaxWidth = 340
                },
                IsChecked = checkedItem,
                IsEnabled = !busy
            };
            cb.Checked += (sender, e) => { excludedKeys.Remove(s.Key); Render(); };
            cb.Unchecked += (sender, e) => { excludedKeys.Add(s.Key); Render(); };
            rowGrid.Children.Add(cb);

            var meta = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string srcName = s.Source == "otzaria" ? "אוצריא" : (s.Source == "pardes" ? "פרדס" : "זית");
            meta.Children.Add(Theme.Pill(srcName, "SUB", "BORDER"));
            var scopeTxt = Theme.T(s.ScopeLabel() + (s.Count > 1 ? " · ×" + Fmt.Num(s.Count) : ""), 11.5, "SUB");
            scopeTxt.Margin = new Thickness(6, 0, 0, 0);
            scopeTxt.VerticalAlignment = VerticalAlignment.Center;
            meta.Children.Add(scopeTxt);
            Grid.SetColumn(meta, 1);
            rowGrid.Children.Add(meta);

            listStack.Children.Add(rowGrid);
        }

        var box = new Border
        {
            Background = Theme.B("FIELD"),
            BorderBrush = Theme.B("BORDER"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            MaxHeight = 200,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listStack,
                Focusable = false
            }
        };
        wrap.Children.Add(box);
        return wrap;
    }

    UIElement GradesReviewSection(List<GradeRow> allGrades, int includedCount)
    {
        var wrap = new StackPanel { Margin = new Thickness(26, 2, 0, 8) };
        var toggleText = (gradesReviewOpen ? "▾ " : "◂ ") + "עיון בדירוגים שיישלחו (" + Fmt.Num(includedCount) + " מתוך " + Fmt.Num(allGrades.Count) + ")";
        var toggleBtn = Theme.Btn(this, toggleText, "Link", () => { gradesReviewOpen = !gradesReviewOpen; Render(); });
        toggleBtn.HorizontalAlignment = HorizontalAlignment.Left;
        wrap.Children.Add(toggleBtn);

        if (!gradesReviewOpen) return wrap;

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        tools.Children.Add(Theme.Btn(this, "בחר הכל", "Link", () => { excludedGradeKeys.Clear(); Render(); }));
        var clearBtn = Theme.Btn(this, "נקה הכל", "Link", () =>
        {
            foreach (var g in allGrades) excludedGradeKeys.Add(g.Key);
            Render();
        });
        clearBtn.Margin = new Thickness(14, 0, 0, 0);
        tools.Children.Add(clearBtn);
        wrap.Children.Add(tools);

        var listStack = new StackPanel();
        foreach (var item in allGrades.AsEnumerable().Reverse())
        {
            var g = item;
            bool checkedItem = !excludedGradeKeys.Contains(g.Key);
            var rowGrid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition());
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textPanel = new StackPanel { MaxWidth = 320 };
            textPanel.Children.Add(new TextBlock
            {
                Text = g.Query,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Theme.B("TEXT")
            });
            string detail = !string.IsNullOrEmpty(g.BookTitle) ? g.BookTitle + (!string.IsNullOrEmpty(g.HeRef) ? " · " + g.HeRef : "")
                          : (!string.IsNullOrEmpty(g.HeRef) ? g.HeRef : ("שורה " + g.LineId));
            textPanel.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Theme.B("SUB")
            });

            var cb = new CheckBox
            {
                Content = textPanel,
                IsChecked = checkedItem,
                IsEnabled = !busy
            };
            cb.Checked += (sender, e) => { excludedGradeKeys.Remove(g.Key); Render(); };
            cb.Unchecked += (sender, e) => { excludedGradeKeys.Add(g.Key); Render(); };
            rowGrid.Children.Add(cb);

            var meta = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string srcName = g.Source == "otzaria" ? "אוצריא" : "זית";
            meta.Children.Add(Theme.Pill(srcName, "SUB", "BORDER"));

            string gradeBadge = g.GradeLabel();
            string badgeBg = g.Grade == 3 ? "ACCENTSOFT" : (g.Grade == 2 ? "ACCENTSOFT" : "REDSOFT");
            string badgeFg = g.Grade == 3 ? "ACCENT" : (g.Grade == 2 ? "ACCENT" : "RED");
            var gradePill = Theme.Pill(gradeBadge, badgeFg, badgeBg);
            gradePill.Margin = new Thickness(6, 0, 0, 0);
            meta.Children.Add(gradePill);

            Grid.SetColumn(meta, 1);
            rowGrid.Children.Add(meta);

            listStack.Children.Add(rowGrid);
        }

        var box = new Border
        {
            Background = Theme.B("FIELD"),
            BorderBrush = Theme.B("BORDER"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            MaxHeight = 220,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listStack,
                Focusable = false
            }
        };
        wrap.Children.Add(box);
        return wrap;
    }

    // ---- Card 3: Telemetry Sent Over Time ----

    UIElement SentStatsCard()
    {
        long totalSearches = Prefs.GetTotalSentSearches();
        long totalGrades = Prefs.GetTotalSentGrades();
        long totalDaf = Prefs.GetTotalSentDaf();
        long totalBatches = Prefs.GetTotalSentBatches();
        long firstSent = Prefs.GetFirstSentAt();
        long lastSent = Prefs.GetLastSentAt();

        var rows = new List<UIElement> { Theme.Title("נתונים שנשלחו לאורך זמן") };

        if (totalBatches == 0 && lastSent == 0)
        {
            var emptyTxt = Theme.T("עדיין לא נשלחו נתונים ממחשב זה. כשתשלח או כשהשליחה האוטומטית תפעל, יופיע כאן סיכום מצטבר של כל החיפושים והדירוגים שנמסרו.", 12.5, "SUB");
            rows.Add(emptyTxt);
            return Theme.Card(rows.ToArray());
        }

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var col1 = new StackPanel();
        col1.Children.Add(Theme.Pair("חיפושים שנשלחו", Fmt.Num(totalSearches)));
        col1.Children.Add(Theme.Pair("דירוגים שנשלחו", Fmt.Num(totalGrades)));
        Grid.SetColumn(col1, 0);
        grid.Children.Add(col1);

        var col2 = new StackPanel();
        col2.Children.Add(Theme.Pair("חבילות שידור", Fmt.Num(totalBatches)));
        if (totalDaf > 0)
            col2.Children.Add(Theme.Pair("שורות צורת הדף", Fmt.Num(totalDaf)));
        Grid.SetColumn(col2, 1);
        grid.Children.Add(col2);

        rows.Add(grid);

        var dates = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (firstSent > 0)
            dates.Children.Add(Theme.Pair("שליחה ראשונה", Fmt.When(firstSent)));
        if (lastSent > 0)
            dates.Children.Add(Theme.Pair("שליחה אחרונה", Fmt.When(lastSent)));
        rows.Add(dates);

        return Theme.Card(rows.ToArray());
    }

    // ---- Card 4: Actions, Auto-Send & Privacy ----

    UIElement ActionsAndPrivacyCard()
    {
        var rows = new List<UIElement> { Theme.Title("שליחה ופרטיות") };

        var pkg = Sources.BuildPackage(scan, zayitOn, otzariaOn, searchesOn, gradesOn, dafOn, excludedKeys, excludedGradeKeys);
        bool hasAnything = !pkg.IsEmpty;
        string serviceUrl = Service.ResolveUrl();
        bool hasUrl = !string.IsNullOrEmpty(serviceUrl);

        // Auto-send toggle
        var cbAuto = new CheckBox
        {
            Content = "שליחה אוטומטית ברקע בהפעלת המחשב ובחיבור לרשת",
            IsChecked = autoSend,
            IsEnabled = !busy,
            Margin = new Thickness(0, 2, 0, 4)
        };
        cbAuto.Checked += (s, e) => { autoSend = true; Prefs.SetAutoSendEnabled(true); Render(); };
        cbAuto.Unchecked += (s, e) => { autoSend = false; Prefs.SetAutoSendEnabled(false); Render(); };
        rows.Add(cbAuto);

        var privacy = Theme.T("המשוב נשלח עם מזהה אקראי, בלי שם, בלי חשבון ובלי הערות או סימניות.", 12.5, "SUB");
        privacy.Margin = new Thickness(26, 0, 0, 8);
        rows.Add(privacy);

        // Sender ID & links
        string senderId = Prefs.GetSender();
        string shortSender = senderId.Length >= 8 ? senderId.Substring(0, 8) : senderId;
        var senderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        var senderLbl = Theme.T("מזהה השולח: " + shortSender, 12.5, "SUB");
        senderLbl.ToolTip = senderId;
        senderLbl.VerticalAlignment = VerticalAlignment.Center;
        senderRow.Children.Add(senderLbl);

        var renewBtn = Theme.Btn(this, "מזהה חדש", "Link", () =>
        {
            Prefs.RenewSender();
            confirmDeleteServer = false;
            statusError = false;
            statusText = "נוצר מזהה שולח אקראי חדש.";
            Render();
        });
        renewBtn.Margin = new Thickness(12, 0, 0, 0);
        senderRow.Children.Add(renewBtn);

        if (hasUrl && !confirmDeleteServer)
        {
            var delBtn = Theme.Btn(this, "מחיקת מה ששלחתי מהשרת", "Link", () =>
            {
                confirmDeleteServer = true;
                Render();
            });
            delBtn.Margin = new Thickness(12, 0, 0, 0);
            senderRow.Children.Add(delBtn);
        }

        var urlBtn = Theme.Btn(this, editingUrl ? "סגירת הגדרת שרת" : "כתובת שרת…", "Link", () =>
        {
            editingUrl = !editingUrl;
            urlDraft = Service.ResolveUrl();
            Render();
        });
        urlBtn.Margin = new Thickness(12, 0, 0, 0);
        senderRow.Children.Add(urlBtn);
        rows.Add(senderRow);

        if (editingUrl)
        {
            rows.Add(UrlConfigBox());
        }

        if (confirmDeleteServer)
        {
            rows.Add(Theme.Note(Theme.G_WARN, "למחוק מהשרת את כל החבילות שנשלחו עם המזהה הזה (" + shortSender + ")?", "RED", "REDSOFT"));
            rows.Add(Theme.Row(
                Theme.Btn(this, "מחיקה מהשרת", "Danger", DoDeleteFromServer),
                Theme.Btn(this, "ביטול", "Outlined", () => { confirmDeleteServer = false; Render(); })));
        }

        // Action buttons
        Button sendBtn = null;
        if (hasUrl)
        {
            sendBtn = Theme.IconBtn(this, Theme.G_SEND, busy ? (busyLabel ?? "שולח…") : "שליחה עכשיו", "Default", DoSendNow);
            sendBtn.IsEnabled = hasAnything && !busy;
        }
        var saveBtn = Theme.IconBtn(this, Theme.G_SAVE, "שמירה לקובץ…", hasUrl ? "Outlined" : "Default", DoSaveToFile);
        saveBtn.IsEnabled = hasAnything && !busy;

        Button openFolderBtn = null;
        if (!string.IsNullOrEmpty(lastSavedFile) && File.Exists(lastSavedFile))
        {
            openFolderBtn = Theme.IconBtn(this, Theme.G_OPEN, "פתיחת התיקייה", "Outlined", () =>
            {
                try { Process.Start("explorer.exe", "/select,\"" + lastSavedFile + "\""); } catch { }
            });
        }

        rows.Add(Theme.Row(sendBtn, saveBtn, openFolderBtn));

        if (!hasUrl && !editingUrl)
        {
            var hint = Theme.T("כתובת שירות המשוב לא מוגדרת כרגע: אפשר לשמור לקובץ ולשלוח אותו, או להגדיר כתובת שרת ב\"כתובת שרת…\".", 12, "SUB");
            hint.Margin = new Thickness(0, 6, 0, 0);
            rows.Add(hint);
        }

        return Theme.Card(rows.ToArray());
    }

    UIElement UrlConfigBox()
    {
        var box = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        box.Children.Add(Theme.T("כתובת שירות המשוב של פרדס (Cloudflare Worker):", 12.5, "SUB"));
        var tb = new TextBox
        {
            Text = urlDraft ?? "",
            FlowDirection = FlowDirection.LeftToRight,
            FontSize = 13,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 4, 0, 4),
            Background = Theme.B("FIELD"),
            Foreground = Theme.B("TEXT"),
            BorderBrush = Theme.B("OUTLINE"),
            BorderThickness = new Thickness(1)
        };
        tb.TextChanged += (s, e) => urlDraft = tb.Text;
        box.Children.Add(tb);
        var saveUrl = Theme.Btn(this, "שמירת כתובת", "Outlined", () =>
        {
            Prefs.Set("feedback_url", (urlDraft ?? "").Trim().TrimEnd('/'));
            editingUrl = false;
            Render();
        });
        box.Children.Add(Theme.Row(saveUrl));
        return box;
    }

    // ---- Background Send / Save / Delete Operations ----

    void DoSendNow()
    {
        if (busy) return;
        var curScan = scan;
        var pkg = Sources.BuildPackage(curScan, zayitOn, otzariaOn, searchesOn, gradesOn, dafOn, excludedKeys, excludedGradeKeys);
        var parts = pkg.Parts();
        if (parts.Count == 0) return;

        busy = true;
        busyLabel = "שולח…";
        statusText = null;
        Render();

        bool zOn = zayitOn, oOn = otzariaOn, sOn = searchesOn, gOn = gradesOn, dOn = dafOn;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string err = Service.Send(parts);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                busy = false;
                if (err == null)
                {
                    Sources.RecordSent(curScan, zOn, oOn, sOn, gOn, dOn, pkg.Searches.Count, pkg.Grades.Count, pkg.DafMarkCount);
                    allFromStart = false;
                    statusError = false;
                    statusText = "המשוב נשלח בהצלחה (" +
                        Fmt.Num(pkg.Searches.Count) + " חיפושים, " +
                        Fmt.Num(pkg.Grades.Count) + " דירוגים" +
                        (pkg.DafMarkCount > 0 ? ", " + Fmt.Num(pkg.DafMarkCount) + " בצורת הדף" : "") + "). תודה!";
                    RescanAndRender();
                }
                else
                {
                    statusError = true;
                    statusText = err;
                    Render();
                }
            }));
        });
    }

    void DoSaveToFile()
    {
        if (busy) return;
        var curScan = scan;
        var pkg = Sources.BuildPackage(curScan, zayitOn, otzariaOn, searchesOn, gradesOn, dafOn, excludedKeys, excludedGradeKeys);
        var parts = pkg.Parts();
        if (parts.Count == 0) return;

        var folder = FolderPicker.Pick(this, "בחירת תיקייה לשמירת קובץ המשוב");
        if (string.IsNullOrEmpty(folder)) return;

        try
        {
            string firstFile = null;
            for (int i = 0; i < parts.Count; i++)
            {
                string f = parts[i].WriteTo(folder, i, parts.Count);
                if (firstFile == null) firstFile = f;
            }
            Sources.RecordSent(curScan, zayitOn, otzariaOn, searchesOn, gradesOn, dafOn, pkg.Searches.Count, pkg.Grades.Count, pkg.DafMarkCount);
            allFromStart = false;
            lastSavedFile = firstFile;
            statusError = false;
            statusText = parts.Count == 1
                ? "נשמר לקובץ: " + Path.GetFileName(firstFile)
                : "נשמרו " + parts.Count + " קבצים בתיקייה: " + folder;
            RescanAndRender();
        }
        catch (Exception ex)
        {
            statusError = true;
            statusText = "שמירת הקובץ נכשלה: " + ex.Message;
            Render();
        }
    }

    void DoDeleteFromServer()
    {
        if (busy) return;
        confirmDeleteServer = false;
        busy = true;
        busyLabel = "מוחק…";
        statusText = null;
        Render();

        string sender = Prefs.GetSender();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string err = Service.DeleteSent(sender);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                busy = false;
                if (err == null)
                {
                    foreach (var src in new[] { "zayit", "otzaria" })
                        foreach (var k in new[] { "searches", "grades", "daf" })
                            Prefs.SetSentAt(src, k, 0L);
                    Prefs.ResetSentStats();
                    statusError = false;
                    statusText = "כל החבילות שנשלחו עם המזהה הזה נמחקו מהשרת.";
                    RescanAndRender();
                }
                else
                {
                    statusError = true;
                    statusText = err;
                    Render();
                }
            }));
        });
    }
}

// Native Windows folder picker (SHBrowseForFolderW in shell32.dll), identical to zayit-move.
static class FolderPicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHBrowseForFolderW(ref BROWSEINFO bi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool SHGetPathFromIDListW(IntPtr pidl, [Out] char[] pszPath);

    [DllImport("ole32.dll")]
    static extern void CoTaskMemFree(IntPtr pv);

    public static string Pick(Window owner, string title)
    {
        var name = Marshal.AllocHGlobal(520);
        var pidl = IntPtr.Zero;
        try
        {
            var bi = new BROWSEINFO
            {
                hwndOwner = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero,
                pszDisplayName = name,
                lpszTitle = title,
                ulFlags = 0x00000041 // BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE
            };
            pidl = SHBrowseForFolderW(ref bi);
            if (pidl == IntPtr.Zero) return null;
            var buf = new char[260];
            return SHGetPathFromIDListW(pidl, buf) ? new string(buf).TrimEnd('\0') : null;
        }
        finally
        {
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
            Marshal.FreeHGlobal(name);
        }
    }
}
