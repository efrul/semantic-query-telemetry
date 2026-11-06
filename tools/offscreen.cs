// Drives the window without showing it: created off-screen and never activated, so nothing appears on the desktop.
// Pictures are saved with RenderTargetBitmap. Built and run by offscreen.ps1, next to a copy of ZayitTelemetry.exe.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

static class Offscreen
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static Window w;
    static string outDir;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: offscreen <output folder> <script file>"); return 2; }
        Console.OutputEncoding = Encoding.UTF8;
        outDir = args[0];
        Directory.CreateDirectory(outDir);
        var app = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZayitTelemetry.exe"));
        w = (Window)Activator.CreateInstance(app.GetType("MainWindow"), true);
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000;
        w.Top = -20000;
        w.Show();
        Wait(1500);

        int failed = 0;
        foreach (var raw in File.ReadAllLines(args[1], Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int sp = line.IndexOf(' ');
            var cmd = sp < 0 ? line : line.Substring(0, sp);
            var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            try { Run(cmd, arg); }
            catch (Exception ex) { failed++; Console.WriteLine("FAILED " + line + ": " + (ex.InnerException ?? ex).Message); }
        }
        typeof(Window).GetMethod("Close").Invoke(w, null);
        return failed == 0 ? 0 : 1;
    }

    static void Run(string cmd, string arg)
    {
        switch (cmd)
        {
            case "click": Click(Find(arg)); Wait(300); break;
            case "wait": Wait(int.Parse(arg)); break;
            case "shot": Shot(Path.Combine(outDir, arg + ".png")); break;
            case "height": w.Height = double.Parse(arg); Wait(300); break;
            case "render": w.GetType().GetMethod("Render", Any).Invoke(w, null); Wait(300); break;
            default: throw new Exception("unknown command");
        }
    }

    static void Wait(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (s, e) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    static IEnumerable<DependencyObject> Tree(DependencyObject o)
    {
        yield return o;
        int n = VisualTreeHelper.GetChildrenCount(o);
        for (int i = 0; i < n; i++)
            foreach (var c in Tree(VisualTreeHelper.GetChild(o, i)))
                yield return c;
    }

    static FrameworkElement FindOrNull(string text)
    {
        w.UpdateLayout();
        foreach (var o in Tree(w))
        {
            var t = o as TextBlock;
            if (t != null && (t.Text == text || t.Text.Contains(text)) && t.IsVisible) return t;
            var c = o as ContentControl;
            if (c != null && c.Content as string == text && c.IsVisible) return c;
        }
        return null;
    }

    static FrameworkElement Find(string text)
    {
        var f = FindOrNull(text);
        if (f == null) throw new Exception("no element shows this text: " + text);
        return f;
    }

    static void Click(DependencyObject target)
    {
        for (DependencyObject o = target; o != null; o = VisualTreeHelper.GetParent(o))
        {
            var cb = o as CheckBox;
            if (cb != null) { cb.IsChecked = !(cb.IsChecked ?? false); return; }
            var b = o as ButtonBase;
            if (b == null) continue;
            b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return;
        }
        throw new Exception("not in a button");
    }

    static void Shot(string file)
    {
        var root = (FrameworkElement)w.Content;
        var source = PresentationSource.FromVisual(w);
        double scale = source != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
        double width = root.ActualWidth, height = root.ActualHeight;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
            if (w.FlowDirection == FlowDirection.RightToLeft) dc.PushTransform(new ScaleTransform(-1, 1, width / 2, 0));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(file)) png.Save(f);
        Console.WriteLine("saved " + Path.GetFileName(file));
    }
}
