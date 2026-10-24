using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using Path = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;

namespace CleanerRAM
{
    class UI
    {
        public static string Exe { get { return Process.GetCurrentProcess().MainModule.FileName; } }
        public static Mutex Mtx;

        static readonly Dictionary<string, string[]> L = new Dictionary<string, string[]>
        {
            {"tab.main", new[]{"Главная", "Home"}},
            {"tab.settings", new[]{"Настройки", "Settings"}},
            {"ram", new[]{"Оперативная память", "Memory in use"}},
            {"used.of", new[]{"{0} из {1} ГБ", "{0} of {1} GB"}},
            {"stat.free", new[]{"Доступно", "Available"}},
            {"stat.freed", new[]{"Освобождено", "Freed"}},
            {"stat.next", new[]{"Автоочистка", "Auto clean"}},
            {"clean", new[]{"Очистить память", "Free up memory"}},
            {"cleaning", new[]{"Очищаю…", "Cleaning…"}},
            {"freed.msg", new[]{"Освобождено {0}", "Freed {0}"}},
            {"sub.on", new[]{"Автоочистка · каждые {0} мин", "Auto clean · every {0} min"}},
            {"sub.off", new[]{"Автоочистка выключена", "Auto clean is off"}},
            {"sec.auto", new[]{"АВТООЧИСТКА", "AUTO CLEAN"}},
            {"auto.enable", new[]{"Включить", "Enabled"}},
            {"auto.interval", new[]{"Интервал", "Interval"}},
            {"auto.threshold", new[]{"Только если занято больше", "Only when usage is above"}},
            {"min", new[]{"мин", "min"}},
            {"always", new[]{"всегда", "always"}},
            {"off", new[]{"выкл", "off"}},
            {"sec.what", new[]{"ЧТО ОЧИЩАТЬ", "WHAT TO CLEAN"}},
            {"w.ws", new[]{"Рабочие наборы процессов", "Process working sets"}},
            {"w.standby", new[]{"Список ожидания (кэш)", "Standby list (cache)"}},
            {"w.modified", new[]{"Изменённые страницы", "Modified page list"}},
            {"w.file", new[]{"Системный файловый кэш", "System file cache"}},
            {"sec.look", new[]{"ОФОРМЛЕНИЕ", "APPEARANCE"}},
            {"look.theme", new[]{"Тема", "Theme"}},
            {"theme.light", new[]{"Светлая", "Light"}},
            {"theme.dark", new[]{"Тёмная", "Dark"}},
            {"look.accent", new[]{"Акцент", "Accent"}},
            {"look.lang", new[]{"Язык", "Language"}},
            {"sec.sys", new[]{"СИСТЕМА", "SYSTEM"}},
            {"sys.autostart", new[]{"Запускать вместе с Windows", "Start with Windows"}},
            {"sys.tray", new[]{"Сворачивать в трей", "Minimize to tray"}},
            {"sys.notify", new[]{"Уведомления об очистке", "Cleanup notifications"}},
            {"admin.no", new[]{"Без прав администратора доступна только часть очистки", "Without admin rights only part of the cleanup works"}},
            {"admin.btn", new[]{"Запустить от админа", "Run as admin"}},
            {"tray.open", new[]{"Открыть", "Open"}},
            {"tray.clean", new[]{"Очистить сейчас", "Clean now"}},
            {"tray.exit", new[]{"Выход", "Exit"}},
            {"gb", new[]{"ГБ", "GB"}},
            {"mb", new[]{"МБ", "MB"}},
        };

        static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        static readonly IEasingFunction EaseQuart = new QuarticEase { EasingMode = EasingMode.EaseOut };
        static readonly IEasingFunction EaseSpring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        static readonly IEasingFunction EaseSine = new SineEase { EasingMode = EasingMode.EaseInOut };

        Window w;
        Settings s;
        WF.NotifyIcon tray;
        DispatcherTimer tick, anim, revert, countT;
        DateTime nextAuto;
        long lastFreed = -1;
        bool busy, admin, exiting, introDone, ambientOn;
        double curPct, tgtPct;
        int lastTrayPct = -1;

        Path ring, spin;
        TextBlock pctText, tbUsed, tbFree, tbFreed, tbNext, tbSub, cleanText;
        FrameworkElement cleanBtn, glow, shimmer, ping, ringBox, root;
        UIElement pageMain, pageSet, autoOpts;
        ScaleTransform rootSc, ringSc;
        TranslateTransform shimTT;
        RotateTransform spinRot;
        FrameworkElement[] blobs;
        TranslateTransform[] blobTT;
        ScaleTransform[] blobSC;
        ScaleTransform pingSc;

        T F<T>(string n) where T : class { return w.FindName(n) as T; }
        string Tr(string k) { string[] v; return L.TryGetValue(k, out v) ? v[s.Lang == "en" ? 1 : 0] : k; }

        static void Go(DependencyObject o, DependencyProperty p, double to, int ms, int delay = 0, IEasingFunction e = null, double? from = null)
        {
            double f = from.HasValue ? from.Value : (double)o.GetValue(p);
            var a = new DoubleAnimation(f, to, TimeSpan.FromMilliseconds(Math.Max(1, ms)));
            a.EasingFunction = e ?? EaseOut;
            if (delay > 0) a.BeginTime = TimeSpan.FromMilliseconds(delay);
            ((IAnimatable)o).BeginAnimation(p, a);
        }

        static TranslateTransform TT(UIElement el)
        {
            var g = el.RenderTransform as TransformGroup;
            if (g != null) foreach (var c in g.Children) { var t = c as TranslateTransform; if (t != null) return t; }
            var tt = el.RenderTransform as TranslateTransform;
            if (tt != null) return tt;
            tt = new TranslateTransform();
            el.RenderTransform = tt;
            return tt;
        }

        static ScaleTransform SC(FrameworkElement el)
        {
            var g = el.RenderTransform as TransformGroup;
            if (g != null) foreach (var c in g.Children) { var t = c as ScaleTransform; if (t != null) return t; }
            var sc = el.RenderTransform as ScaleTransform;
            if (sc != null) return sc;
            sc = new ScaleTransform();
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = sc;
            return sc;
        }

        static void Scale(ScaleTransform sc, double to, int ms = 180, IEasingFunction e = null)
        {
            Go(sc, ScaleTransform.ScaleXProperty, to, ms, 0, e);
            Go(sc, ScaleTransform.ScaleYProperty, to, ms, 0, e);
        }

        static void Rise(UIElement el, int delay, double dy = 14)
        {
            var tt = TT(el);
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 0;
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = dy;
            Go(el, UIElement.OpacityProperty, 1, 320, delay, EaseOut, 0);
            Go(tt, TranslateTransform.YProperty, 0, 480, delay, EaseQuart, dy);
        }

        void Stagger(IEnumerable<UIElement> items, int start = 0, int step = 40)
        {
            int i = 0;
            foreach (var el in items) Rise(el, start + Math.Min(i++, 8) * step);
        }

        static void Swap(TextBlock tb, string text)
        {
            if (tb.Text == text) return;
            if (string.IsNullOrEmpty(tb.Text) || tb.Opacity < 0.05) { tb.Text = text; return; }
            var a = new DoubleAnimation(tb.Opacity, 0, TimeSpan.FromMilliseconds(80));
            a.Completed += delegate { tb.Text = text; Go(tb, UIElement.OpacityProperty, 1, 160, 0, EaseOut, 0); };
            tb.BeginAnimation(UIElement.OpacityProperty, a);
        }

        void Click(FrameworkElement e, Action a, double hover = 1.0, double press = 0.96)
        {
            var sc = SC(e);
            bool down = false;
            e.MouseEnter += (o, ev) => { if (!down && hover != 1.0) Scale(sc, hover, 180, EaseSpring); };
            e.MouseLeave += (o, ev) => { if (!down) Scale(sc, 1, 200); };
            e.MouseLeftButtonDown += (o, ev) =>
            {
                ev.Handled = true; down = true;
                e.CaptureMouse();
                Scale(sc, press, 100);
            };
            e.MouseLeftButtonUp += (o, ev) =>
            {
                bool over = e.IsMouseOver;
                down = false;
                e.ReleaseMouseCapture();
                Scale(sc, over ? hover : 1, 280, EaseSpring);
                if (over) a();
            };
        }

        public UI(bool hidden, bool withTray)
        {
            s = Settings.Load();
            admin = Cleaner.IsAdmin();
            using (Stream st = typeof(UI).Assembly.GetManifestResourceStream("ui.xaml"))
                w = (Window)XamlReader.Load(st);
            Program.Tr("xaml ok");

            root = F<FrameworkElement>("Root");
            ring = F<Path>("Ring"); spin = F<Path>("Spin");
            pctText = F<TextBlock>("PctText"); tbUsed = F<TextBlock>("UsedText"); tbFree = F<TextBlock>("FreeText");
            tbFreed = F<TextBlock>("FreedText"); tbNext = F<TextBlock>("NextText"); tbSub = F<TextBlock>("SubText");
            cleanText = F<TextBlock>("CleanText");
            cleanBtn = F<FrameworkElement>("CleanBtn"); glow = F<FrameworkElement>("Glow"); shimmer = F<FrameworkElement>("Shimmer");
            ping = F<FrameworkElement>("Ping"); ringBox = F<FrameworkElement>("RingBox");
            pageMain = F<UIElement>("PageMain"); pageSet = F<UIElement>("PageSet"); autoOpts = F<UIElement>("AutoOpts");

            rootSc = new ScaleTransform();
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = rootSc;
            ringSc = new ScaleTransform(); ringBox.RenderTransform = ringSc;
            spinRot = new RotateTransform(); spin.RenderTransform = spinRot;
            shimTT = new TranslateTransform(); shimmer.RenderTransform = shimTT;
            pingSc = new ScaleTransform(); ping.RenderTransform = pingSc;

            blobs = new[] { F<FrameworkElement>("BlobA"), F<FrameworkElement>("BlobB"), F<FrameworkElement>("BlobC"), F<FrameworkElement>("BlobD"), F<FrameworkElement>("BlobE") };
            blobTT = new TranslateTransform[blobs.Length];
            blobSC = new ScaleTransform[blobs.Length];
            for (int i = 0; i < blobs.Length; i++)
            {
                blobTT[i] = new TranslateTransform(); blobSC[i] = new ScaleTransform();
                blobs[i].RenderTransformOrigin = new Point(0.5, 0.5);
                blobs[i].RenderTransform = new TransformGroup { Children = { blobSC[i], blobTT[i] } };
            }

            cleanBtn.RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } };

            root.SizeChanged += delegate { root.Clip = new RectangleGeometry(new Rect(root.RenderSize), 26, 26); };
            cleanBtn.SizeChanged += delegate { cleanBtn.Clip = new RectangleGeometry(new Rect(cleanBtn.RenderSize), 20, 20); };
            F<Grid>("Header").MouseLeftButtonDown += (o, e) => { try { w.DragMove(); } catch { } };

            Click(F<FrameworkElement>("MinBtn"), () => { if (s.Tray) HideWin(); else w.WindowState = WindowState.Minimized; }, 1.1, 0.88);
            Click(F<FrameworkElement>("CloseBtn"), () => { if (s.Tray) HideWin(); else Exit(); }, 1.1, 0.88);
            Click(cleanBtn, () => DoClean(false), 1.02, 0.965);
            Click(F<FrameworkElement>("AdminBtn"), RestartAdmin, 1.06, 0.94);
            F<UIElement>("AdminBar").Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

            F<RadioButton>("TabMain").Checked += delegate { ShowPage(true); };
            F<RadioButton>("TabSet").Checked += delegate { ShowPage(false); };
            Segment(F<Grid>("TabHost"), F<Border>("TabInd"), F<RadioButton>("TabMain"), F<RadioButton>("TabSet"));
            Segment(F<Grid>("ThemeHost"), F<Border>("ThemeInd"), F<RadioButton>("ThemeL"), F<RadioButton>("ThemeD"));
            Segment(F<Grid>("LangHost"), F<Border>("LangInd"), F<RadioButton>("LangRu"), F<RadioButton>("LangEn"));

            Sw("AutoT", s.Auto, v => { s.Auto = v; ScheduleNext(); DimAuto(true); UpdateSub(); });
            Sw("WsT", s.Working, v => s.Working = v);
            Sw("StandbyT", s.Standby, v => s.Standby = v);
            Sw("ModT", s.Modified, v => s.Modified = v);
            Sw("FileT", s.FileCache, v => s.FileCache = v);
            Sw("AutostartT", s.Autostart, v => { s.Autostart = v; Autostart.Set(v, admin); });
            Sw("TrayT", s.Tray, v => s.Tray = v);
            Sw("NotifyT", s.Notify, v => s.Notify = v);
            DimAuto(false);

            var iv = F<Slider>("IntervalS");
            iv.Value = s.Interval;
            iv.ValueChanged += delegate { s.Interval = (int)iv.Value; ScheduleNext(); UpdateLabels(); UpdateSub(); s.Save(); };
            var th = F<Slider>("ThreshS");
            th.Value = s.Threshold;
            th.ValueChanged += delegate { s.Threshold = (int)th.Value; UpdateLabels(); s.Save(); };

            Radio("ThemeL", s.Theme != "dark", () => { s.Theme = "light"; ApplyTheme(true); });
            Radio("ThemeD", s.Theme == "dark", () => { s.Theme = "dark"; ApplyTheme(true); });
            Radio("LangRu", s.Lang != "en", () => { s.Lang = "ru"; ApplyLang(); Flash(); });
            Radio("LangEn", s.Lang == "en", () => { s.Lang = "en"; ApplyLang(); Flash(); });
            foreach (var child in F<Panel>("AccentPanel").Children)
            {
                var rb = (RadioButton)child;
                string hex = ((SolidColorBrush)rb.Background).Color.ToString();
                rb.IsChecked = string.Equals(hex, s.Accent, StringComparison.OrdinalIgnoreCase);
                rb.Checked += delegate { s.Accent = hex; ApplyTheme(true); s.Save(); };
            }

            SmoothScroll(F<ScrollViewer>("PageSet"));

            anim = new DispatcherTimer(DispatcherPriority.Render);
            anim.Interval = TimeSpan.FromMilliseconds(16);
            anim.Tick += delegate
            {
                curPct += (tgtPct - curPct) * 0.14;
                if (Math.Abs(tgtPct - curPct) < 0.0004) { curPct = tgtPct; anim.Stop(); }
                DrawRing();
            };

            tick = new DispatcherTimer();
            tick.Interval = TimeSpan.FromSeconds(1);
            tick.Tick += delegate { Tick(); };

            if (withTray) InitTray();
            ApplyTheme(false);
            ApplyLang();
            ScheduleNext();
            Tick();
            tick.Start();

            w.IsVisibleChanged += delegate
            {
                if (w.IsVisible)
                {
                    Go(w, UIElement.OpacityProperty, 1, 240, 0, EaseOut, 0);
                    Go(rootSc, ScaleTransform.ScaleXProperty, 1, 450, 0, EaseSpring, 0.95);
                    Go(rootSc, ScaleTransform.ScaleYProperty, 1, 450, 0, EaseSpring, 0.95);
                    if (!introDone) Intro();
                    StartAmbient();
                    Tick();
                }
                else StopAmbient();
            };
            w.StateChanged += delegate { if (w.WindowState == WindowState.Minimized) StopAmbient(); else if (w.IsVisible) { StartAmbient(); Tick(); } };
            w.Closing += (o, e) => { if (!exiting && s.Tray && tray != null) { e.Cancel = true; HideWin(); } };

            w.Opacity = 0;
            if (!hidden) w.Show();
        }

        public Window Window { get { return w; } }

        void Segment(Grid host, Border ind, params RadioButton[] rbs)
        {
            var tt = new TranslateTransform();
            ind.RenderTransform = tt;
            Action<bool> place = animate =>
            {
                RadioButton cur = null;
                foreach (var r in rbs) if (r.IsChecked == true) cur = r;
                if (cur == null || cur.ActualWidth < 1) return;
                double x = cur.TranslatePoint(new Point(0, 0), host).X;
                if (animate && ind.Width > 1)
                {
                    Go(tt, TranslateTransform.XProperty, x, 360, 0, EaseSpring);
                    Go(ind, FrameworkElement.WidthProperty, cur.ActualWidth, 320, 0, EaseOut);
                }
                else
                {
                    tt.BeginAnimation(TranslateTransform.XProperty, null); tt.X = x;
                    ind.BeginAnimation(FrameworkElement.WidthProperty, null); ind.Width = cur.ActualWidth;
                }
            };
            foreach (var r in rbs) r.Checked += delegate { place(true); };
            host.SizeChanged += delegate { place(false); };
            foreach (var r in rbs) r.SizeChanged += delegate { place(false); };
        }

        void Sw(string n, bool val, Action<bool> set)
        {
            var c = F<CheckBox>(n);
            c.IsChecked = val;
            RoutedEventHandler h = (o, e) => { set(c.IsChecked == true); s.Save(); };
            c.Checked += h; c.Unchecked += h;
        }

        void Radio(string n, bool val, Action a)
        {
            var r = F<RadioButton>(n);
            r.IsChecked = val;
            r.Checked += delegate { a(); s.Save(); };
        }

        void DimAuto(bool animate)
        {
            autoOpts.IsEnabled = s.Auto;
            double o = s.Auto ? 1 : 0.35;
            if (animate) Go(autoOpts, UIElement.OpacityProperty, o, 260);
            else autoOpts.Opacity = o;
        }

        void ShowPage(bool main)
        {
            var a = main ? pageMain : pageSet;
            var b = main ? pageSet : pageMain;
            b.Visibility = Visibility.Collapsed;
            a.Visibility = Visibility.Visible;
            Stagger(PageItems(main), 0, main ? 45 : 35);
        }

        IEnumerable<UIElement> PageItems(bool main)
        {
            Panel p = main ? (Panel)pageMain : (Panel)F<StackPanel>("SetList");
            foreach (UIElement c in p.Children) yield return c;
        }

        void Flash()
        {
            Go(pageMain, UIElement.OpacityProperty, 1, 280, 0, EaseOut, 0.3);
            Go(pageSet, UIElement.OpacityProperty, 1, 280, 0, EaseOut, 0.3);
        }

        void Intro()
        {
            introDone = true;
            Rise(F<UIElement>("Brand"), 40, -10);
            Rise(F<UIElement>("TabBar"), 100, 10);
            Stagger(PageItems(true), 160, 50);
        }

        // ============ Liquid Glass Анимация фона (GPU RadialGradient) ============
        void StartAmbient()
        {
            if (ambientOn) return;
            ambientOn = true;
            Flow(0, 120, 140, 14, 0, 0.22);
            Flow(1, -130, -120, 18, 4, 0.20);
            Flow(2, 110, -150, 22, 8, 0.24);
            Flow(3, 140, 130, 16, 3, 0.18);
            Flow(4, -110, 140, 20, 10, 0.20);

            var a = new DoubleAnimation(1, 2.6, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseOut };
            pingSc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            pingSc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            var o = new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever };
            ping.BeginAnimation(UIElement.OpacityProperty, o);
        }

        void StopAmbient()
        {
            if (!ambientOn) return;
            ambientOn = false;
            for (int i = 0; i < blobTT.Length; i++)
            {
                blobTT[i].BeginAnimation(TranslateTransform.XProperty, null);
                blobTT[i].BeginAnimation(TranslateTransform.YProperty, null);
                blobSC[i].BeginAnimation(ScaleTransform.ScaleXProperty, null);
                blobSC[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            pingSc.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            pingSc.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ping.BeginAnimation(UIElement.OpacityProperty, null);
        }

        void Flow(int i, double dx, double dy, int sec, int offset, double breathe)
        {
            var bt = TimeSpan.FromSeconds(-offset);
            var ax = new DoubleAnimation(-dx, dx, TimeSpan.FromSeconds(sec)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseSine, BeginTime = bt };
            var ay = new DoubleAnimation(-dy, dy, TimeSpan.FromSeconds(sec * 1.35)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseSine, BeginTime = bt };
            var sx = new DoubleAnimation(1 - breathe * 0.4, 1 + breathe, TimeSpan.FromSeconds(sec * 0.65)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseSine, BeginTime = bt };
            var sy = new DoubleAnimation(1 + breathe, 1 - breathe * 0.4, TimeSpan.FromSeconds(sec * 0.85)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseSine, BeginTime = bt };
            blobTT[i].BeginAnimation(TranslateTransform.XProperty, ax);
            blobTT[i].BeginAnimation(TranslateTransform.YProperty, ay);
            blobSC[i].BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            blobSC[i].BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }

        // ============ 60 FPS Плавный скролл настроек ============
        void SmoothScroll(ScrollViewer sv)
        {
            double target = 0;
            var t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            t.Tick += delegate
            {
                double d = target - sv.VerticalOffset;
                if (Math.Abs(d) < 0.4)
                {
                    sv.ScrollToVerticalOffset(target);
                    t.Stop();
                }
                else
                {
                    sv.ScrollToVerticalOffset(sv.VerticalOffset + d * 0.35); // отзывчивый коэффициент сглаживания
                }
            };
            sv.PreviewMouseWheel += (o, e) =>
            {
                if (!t.IsEnabled) target = sv.VerticalOffset;
                target = Math.Max(0, Math.Min(sv.ScrollableHeight, target - e.Delta * 0.75));
                t.Start();
                e.Handled = true;
            };
        }

        // ============ Тема и Язык ============
        static Color Hex(string h) { return (Color)ColorConverter.ConvertFromString(h); }
        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        RadialGradientBrush Radial(Color center, double maxAlpha = 0.8)
        {
            var b = new RadialGradientBrush();
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(center.A * maxAlpha), center.R, center.G, center.B), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(center.A * maxAlpha * 0.5), center.R, center.G, center.B), 0.45));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0, center.R, center.G, center.B), 1.0));
            return b;
        }

        void SetBrush(string key, Color c, bool animate)
        {
            var b = w.Resources[key] as SolidColorBrush;
            if (b == null || b.IsFrozen) { w.Resources[key] = new SolidColorBrush(c); return; }
            if (!animate) { b.BeginAnimation(SolidColorBrush.ColorProperty, null); b.Color = c; return; }
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(b.Color, c, TimeSpan.FromMilliseconds(450)) { EasingFunction = EaseOut });
        }

        void ApplyTheme(bool animate)
        {
            bool dark = s.Theme == "dark";
            Color acc;
            try { acc = Hex(s.Accent); } catch { acc = Hex("#FFFF6A00"); }
            Color accLight = Mix(acc, Colors.White, 0.45);

            SetBrush("Bg", Hex(dark ? "#12100E" : "#F5F3EF"), animate);
            SetBrush("Glass", Hex(dark ? "#18FFFFFF" : "#70FFFFFF"), animate);
            SetBrush("GlassHover", Hex(dark ? "#26FFFFFF" : "#99FFFFFF"), animate);
            SetBrush("GlassStrong", Hex(dark ? "#30FFFFFF" : "#E8FFFFFF"), animate);
            SetBrush("Ink", Hex(dark ? "#F5F2ED" : "#181512"), animate);
            SetBrush("Sub", Hex(dark ? "#8E877F" : "#7D746A"), animate);
            SetBrush("Track", Hex(dark ? "#1AFFFFFF" : "#12000000"), animate);
            SetBrush("Accent", acc, animate);
            SetBrush("Blob1", acc, animate);
            SetBrush("Blob2", accLight, animate);

            w.Resources["Edge"] = new LinearGradientBrush(
                Color.FromArgb((byte)(dark ? 0x30 : 0x85), 255, 255, 255),
                Color.FromArgb((byte)(dark ? 0x08 : 0x25), 255, 255, 255),
                new Point(0, 0), new Point(1, 1));

            // Обновляем аппаратные градиенты пятен без оверхеда масок
            ((Shape)blobs[0]).Fill = Radial(acc, dark ? 0.65 : 0.85);
            ((Shape)blobs[1]).Fill = Radial(accLight, dark ? 0.50 : 0.80);
            ((Shape)blobs[2]).Fill = Radial(acc, dark ? 0.40 : 0.60);
            ((Shape)blobs[3]).Fill = Radial(dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(200, 255, 255, 255), dark ? 0.3 : 0.7);
            ((Shape)blobs[4]).Fill = Radial(dark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(180, 255, 255, 255), dark ? 0.25 : 0.6);

            ((Shape)glow).Fill = Radial(acc, 0.75);

            int ms = animate ? 450 : 1;
            Go(glow, UIElement.OpacityProperty, dark ? 0.55 : 0.45, ms);
        }

        void ApplyLang()
        {
            Walk(w);
            UpdateLabels();
            UpdateSub();
            lastTrayPct = -1;
            Tick();
            if (tray != null) BuildMenu();
        }

        void Walk(DependencyObject d)
        {
            var tb = d as TextBlock;
            if (tb != null)
            {
                var k = tb.Tag as string;
                if (k != null && k.StartsWith("@")) tb.Text = Tr(k.Substring(1));
            }
            foreach (object c in LogicalTreeHelper.GetChildren(d))
            {
                var dd = c as DependencyObject;
                if (dd != null) Walk(dd);
            }
        }

        void UpdateLabels()
        {
            F<TextBlock>("IntervalText").Text = s.Interval + " " + Tr("min");
            F<TextBlock>("ThreshText").Text = s.Threshold == 0 ? Tr("always") : s.Threshold + "%";
        }

        void UpdateSub()
        {
            Swap(tbSub, s.Auto ? string.Format(Tr("sub.on"), s.Interval) : Tr("sub.off"));
        }

        string Sz(long b)
        {
            if (b < 0) b = 0;
            double mb = b / 1048576.0;
            if (mb >= 1024) return (mb / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " " + Tr("gb");
            return mb.ToString("0", CultureInfo.CurrentCulture) + " " + Tr("mb");
        }

        void DrawRing()
        {
            double r = 113, c = 120;
            double p = Math.Max(0.0005, Math.Min(0.9995, curPct));
            double a = p * 2 * Math.PI;
            var fig = new PathFigure { StartPoint = new Point(c, c - r) };
            fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new System.Windows.Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            ring.Data = g;
            pctText.Text = ((int)Math.Round(curPct * 100)).ToString();
        }

        // ============ Логика очистки и таймеров ============
        void ScheduleNext() { nextAuto = DateTime.Now.AddMinutes(s.Interval); }

        void Tick()
        {
            ulong tot, av; uint load;
            Cleaner.Snapshot(out tot, out av, out load);
            double used = tot - av;
            tgtPct = tot == 0 ? 0 : used / tot;
            int pct = (int)Math.Round(tgtPct * 100);

            if (w.IsVisible && w.WindowState != WindowState.Minimized)
            {
                if (!anim.IsEnabled && Math.Abs(tgtPct - curPct) > 0.0004) anim.Start();
                tbUsed.Text = string.Format(Tr("used.of"),
                    (used / 1073741824.0).ToString("0.0", CultureInfo.CurrentCulture),
                    (tot / 1073741824.0).ToString("0", CultureInfo.CurrentCulture));
                tbFree.Text = Sz((long)av);
                if (countT == null || !countT.IsEnabled) tbFreed.Text = lastFreed < 0 ? "—" : Sz(lastFreed);
                if (s.Auto)
                {
                    var left = nextAuto - DateTime.Now;
                    if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                    tbNext.Text = ((int)left.TotalMinutes).ToString("00") + ":" + left.Seconds.ToString("00");
                }
                else tbNext.Text = Tr("off");
            }

            if (tray != null && pct != lastTrayPct) { lastTrayPct = pct; tray.Text = "CleanerRAM · " + pct + "%"; }

            if (s.Auto && !busy && DateTime.Now >= nextAuto)
            {
                ScheduleNext();
                if (load >= s.Threshold) DoClean(true);
            }
        }

        void BusyFx(bool on)
        {
            if (on)
            {
                Go(spin, UIElement.OpacityProperty, 0.85, 200);
                spinRot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.0)) { RepeatBehavior = RepeatBehavior.Forever });
                Go(shimmer, UIElement.OpacityProperty, 1, 180);
                shimTT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-120, Math.Max(320, cleanBtn.ActualWidth + 20), TimeSpan.FromSeconds(0.95)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseSine });
                var g = new DoubleAnimation(0.5, 0.95, TimeSpan.FromSeconds(0.65)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseSine };
                glow.BeginAnimation(UIElement.OpacityProperty, g);
                Go(cleanBtn, UIElement.OpacityProperty, 0.94, 180);
                Swap(cleanText, Tr("cleaning"));
            }
            else
            {
                Go(spin, UIElement.OpacityProperty, 0, 260);
                Go(shimmer, UIElement.OpacityProperty, 0, 260);
                Go(glow, UIElement.OpacityProperty, s.Theme == "dark" ? 0.55 : 0.45, 360);
                Go(cleanBtn, UIElement.OpacityProperty, 1, 220);
                var stop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
                stop.Tick += delegate
                {
                    stop.Stop();
                    if (busy) return;
                    spinRot.BeginAnimation(RotateTransform.AngleProperty, null);
                    shimTT.BeginAnimation(TranslateTransform.XProperty, null);
                };
                stop.Start();
            }
        }

        void PopRing()
        {
            var kf = new DoubleAnimationUsingKeyFrames();
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(550)), new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 6 }));
            ringSc.BeginAnimation(ScaleTransform.ScaleXProperty, kf);
            ringSc.BeginAnimation(ScaleTransform.ScaleYProperty, kf);
        }

        void CountUp(long target)
        {
            if (countT != null) countT.Stop();
            var t0 = DateTime.UtcNow;
            countT = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            countT.Tick += delegate
            {
                double k = Math.Min(1, (DateTime.UtcNow - t0).TotalMilliseconds / 750);
                k = 1 - Math.Pow(1 - k, 3);
                tbFreed.Text = Sz((long)(target * k));
                if (k >= 1) countT.Stop();
            };
            countT.Start();
        }

        void DoClean(bool auto)
        {
            if (busy) return;
            busy = true;
            if (revert != null) revert.Stop();
            BusyFx(true);
            bool ws = s.Working, sb = s.Standby, md = s.Modified, fc = s.FileCache;
            ThreadPool.QueueUserWorkItem(delegate
            {
                long freed = 0;
                try { freed = Cleaner.Clean(ws, sb, md, fc, auto); } catch { }
                w.Dispatcher.BeginInvoke(new Action(() =>
                {
                    busy = false;
                    lastFreed = Math.Max(0, freed);
                    BusyFx(false);
                    string msg = string.Format(Tr("freed.msg"), Sz(lastFreed));
                    Swap(cleanText, msg);
                    CountUp(lastFreed);
                    PopRing();
                    revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
                    revert.Tick += delegate { revert.Stop(); Swap(cleanText, Tr("clean")); };
                    revert.Start();
                    Tick();
                    if (auto && s.Notify && tray != null && !w.IsVisible)
                        tray.ShowBalloonTip(2500, "CleanerRAM", msg, WF.ToolTipIcon.None);
                }));
            });
        }

        void InitTray()
        {
            tray = new WF.NotifyIcon();
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Exe); }
            catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            tray.Text = "CleanerRAM";
            tray.MouseClick += (o, e) => { if (e.Button == WF.MouseButtons.Left) ShowWin(); };
            BuildMenu();
            tray.Visible = true;
        }

        void BuildMenu()
        {
            var m = new WF.ContextMenuStrip();
            m.Font = new System.Drawing.Font("Segoe UI", 9.5f);
            m.ShowImageMargin = false;
            m.Items.Add(Tr("tray.open"), null, delegate { ShowWin(); });
            m.Items.Add(Tr("tray.clean"), null, delegate { DoClean(true); });
            m.Items.Add(new WF.ToolStripSeparator());
            m.Items.Add(Tr("tray.exit"), null, delegate { Exit(); });
            var old = tray.ContextMenuStrip;
            tray.ContextMenuStrip = m;
            if (old != null) old.Dispose();
        }

        void ShowWin()
        {
            if (!w.IsVisible) w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        }

        void HideWin()
        {
            var a = new DoubleAnimation(w.Opacity, 0, TimeSpan.FromMilliseconds(160));
            a.Completed += delegate { w.Hide(); };
            w.BeginAnimation(UIElement.OpacityProperty, a);
            Go(rootSc, ScaleTransform.ScaleXProperty, 0.96, 160);
            Go(rootSc, ScaleTransform.ScaleYProperty, 0.96, 160);
        }

        void Exit()
        {
            exiting = true;
            s.Save();
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            Application.Current.Shutdown();
        }

        void RestartAdmin()
        {
            try
            {
                if (Mtx != null) Mtx.ReleaseMutex();
                var psi = new ProcessStartInfo(Exe) { Verb = "runas", UseShellExecute = true };
                Process.Start(psi);
                Exit();
            }
            catch
            {
                if (Mtx != null) Mtx.WaitOne(0);
            }
        }

        // ---------- Render screenshots for test ----------
        public void Shots(string prefix)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -4000; w.Top = 0; w.ShowInTaskbar = false;
            w.Show();
            var steps = new List<Action>
            {
                () => Save(prefix + "_main.png"),
                () => { F<RadioButton>("TabSet").IsChecked = true; },
                () => Save(prefix + "_settings.png"),
                () => { F<ScrollViewer>("PageSet").ScrollToEnd(); },
                () => Save(prefix + "_settings2.png"),
                () => { F<RadioButton>("TabMain").IsChecked = true; F<RadioButton>("ThemeD").IsChecked = true; },
                () => Save(prefix + "_dark.png"),
                () => { Application.Current.Shutdown(); },
            };
            int i = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            t.Tick += delegate { if (i < steps.Count) steps[i++](); else t.Stop(); };
            t.Start();
        }

        void Save(string file)
        {
            var rt = F<Grid>("Root");
            var rtb = new RenderTargetBitmap((int)(rt.ActualWidth * 1.5), (int)(rt.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            rtb.Render(rt);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }
    }
}
