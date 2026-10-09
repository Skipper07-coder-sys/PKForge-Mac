#if PKF_AUTOMATION
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using PKForge.App.Services;
using PKForge.App.ViewModels;

namespace PKForge.App;

/// <summary>
/// Test-build remote control (tools/pkf). Compiled only with PKF_AUTOMATION and started only when
/// PKFORGE_AUTOMATION_PORT is set; it listens on loopback. One command per line, answered with
/// "ok &lt;ms&gt;" or "err &lt;message&gt;", body lines, then a line "&lt;&lt;END&gt;&gt;".
/// Presses go through MacPadInput, the same path as the keyboard and controllers, and the screen
/// is read back as text (visible labels, buttons, fields), so a test needs no screenshots.
/// </summary>
internal static class Automation
{
    private const string End = "<<END>>";

    public static void StartIfRequested()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("PKFORGE_AUTOMATION_PORT"), out var port)) return;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        AppLog.Info("automation", $"Listening on 127.0.0.1:{port}");
        // Lifecycle as the Mac reports it, so tests can see what pauses the app (Android pauses on leave; a Mac may not).
        foreach (var (name, note) in new[]
        {
            ("scene: will deactivate", UIKit.UIScene.WillDeactivateNotification),
            ("scene: did activate", UIKit.UIScene.DidActivateNotification),
            ("scene: did enter background", UIKit.UIScene.DidEnterBackgroundNotification),
            ("scene: will enter foreground", UIKit.UIScene.WillEnterForegroundNotification),
        })
            Foundation.NSNotificationCenter.DefaultCenter.AddObserver(note, _ => Perf.Mark(name));
        App.Suspended += () => Perf.Mark("app: OnSleep");
        App.Resumed += () => Perf.Mark("app: OnResume");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => ServeAsync(client));
            }
        });
    }

    private static async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            var started = Stopwatch.GetTimestamp();
            string reply;
            try
            {
                var body = await RunAsync(Tokenize(line));
                reply = $"ok {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0}\n{body}";
            }
            catch (Exception error)
            {
                reply = $"err {error.GetType().Name}: {error.Message}".Replace('\n', ' ') + "\n";
            }
            await writer.WriteAsync(reply.EndsWith('\n') || reply.Length == 0 ? reply : reply + "\n");
            await writer.WriteLineAsync(End);
            if (line.Trim() == "quit") break;
        }
    }

    private static List<string> Tokenize(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in line.Trim())
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    private static Task<string> OnMain(Func<string> work) => MainThread.InvokeOnMainThreadAsync(work);

    private static Task<string> OnMain(Func<Task<string>> work) => MainThread.InvokeOnMainThreadAsync(work);

    private static IServiceProvider Services =>
        IPlatformApplication.Current?.Services ?? throw new InvalidOperationException("services not ready");

    private static async Task<string> RunAsync(List<string> args)
    {
        var verb = args.Count > 0 ? args[0].ToLowerInvariant() : "";
        string Arg(int i, string fallback = "") => args.Count > i ? args[i] : fallback;
        int IntArg(int i, int fallback) => int.TryParse(Arg(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        switch (verb)
        {
            case "ping": return "pong";
            case "info":
                return $"pid {Environment.ProcessId}\ndata {AppPaths.Data}\ncache {AppPaths.Cache}\n" +
                       $"bundle {Foundation.NSBundle.MainBundle.BundleIdentifier}\nuptime {Perf.Now:0}";
            case "press":
            {
                var button = Enum.Parse<PadButton>(Arg(1), ignoreCase: true);
                var times = Math.Max(1, IntArg(2, 1));
                return await OnMain(() =>
                {
                    var consumed = 0;
                    for (var i = 0; i < times; i++)
                    {
                        if (MacPadInput.Press(button)) consumed++;
                        MacPadInput.Release(button);
                    }
                    return $"consumed {consumed}/{times}";
                });
            }
            case "key":
            {
                // A keyboard key through the same resolver as real key presses: one character
                // ("y", "+", "]") as the layout types it, or a key name (return esc tab delete space pageup pagedown up down left right).
                var name = Arg(1);
                var usage = name.ToLowerInvariant() switch
                {
                    "return" => UIKit.UIKeyboardHidUsage.KeyboardReturnOrEnter,
                    "esc" => UIKit.UIKeyboardHidUsage.KeyboardEscape,
                    "tab" => UIKit.UIKeyboardHidUsage.KeyboardTab,
                    "delete" => UIKit.UIKeyboardHidUsage.KeyboardDeleteOrBackspace,
                    "space" => UIKit.UIKeyboardHidUsage.KeyboardSpacebar,
                    "pageup" => UIKit.UIKeyboardHidUsage.KeyboardPageUp,
                    "pagedown" => UIKit.UIKeyboardHidUsage.KeyboardPageDown,
                    "up" => UIKit.UIKeyboardHidUsage.KeyboardUpArrow,
                    "down" => UIKit.UIKeyboardHidUsage.KeyboardDownArrow,
                    "left" => UIKit.UIKeyboardHidUsage.KeyboardLeftArrow,
                    "right" => UIKit.UIKeyboardHidUsage.KeyboardRightArrow,
                    _ => (UIKit.UIKeyboardHidUsage)0,
                };
                var button = MacPadInput.Resolve(usage, usage == 0 ? name : null);
                if (button is not { } pressed) return "no button";
                return await OnMain(() =>
                {
                    var consumed = MacPadInput.Press(pressed);
                    MacPadInput.Release(pressed);
                    return $"{pressed} {(consumed ? "consumed" : "ignored")}";
                });
            }
            case "rightclick":
            {
                // A right-click on the centre of a storage slot, through the gesture's own handler.
                var slot = IntArg(1, 0);
                return await OnMain(() =>
                {
                    var page = TopPage() as Views.BoxBrowserPage ?? throw new InvalidOperationException("not on the storage screen");
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var canvas = (SkiaSharp.Views.Maui.Controls.SKCanvasView)typeof(Views.BoxBrowserPage).GetField("_canvas", flags)!.GetValue(page)!;
                    var box = Services.GetRequiredService<BoxBrowserViewModel>();
                    var scale = canvas.CanvasSize.Width / canvas.Width;
                    var center = box.BoxIndex == -1
                        ? throw new InvalidOperationException("rightclick is wired for box slots")
                        : Views.BoxGridRenderer.SlotRect(canvas.CanvasSize, slot);
                    typeof(Views.BoxBrowserPage).GetMethod("OpenMenuAt", flags)!.Invoke(page, [new Point(center.MidX / scale, center.MidY / scale)]);
                    return $"right-clicked at {center.MidX / scale:0},{center.MidY / scale:0} pt";
                });
            }
            case "drag":
            {
                // drag <fromSlot> <toSlot> [pages]: press on a slot, move, rest on the right (pages > 0)
                // or left edge until the boxes paged that often, then move to the target and let go.
                // Real SKTouch events into the grid's own handler. A target of -1 lets go off the grid.
                var from = IntArg(1, 0);
                var to = IntArg(2, 0);
                var pages = IntArg(3, 0);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                SkiaSharp.Views.Maui.Controls.SKCanvasView? canvas = null;
                Views.BoxBrowserPage? page = null;
                SkiaSharp.SKPoint Center(int slot)
                {
                    var r = Services.GetRequiredService<BoxBrowserViewModel>().BoxIndex == -1
                        ? Views.PartyView.SlotRect(new SkiaSharp.SKImageInfo((int)canvas!.CanvasSize.Width, (int)canvas.CanvasSize.Height), slot)
                        : Views.BoxGridRenderer.SlotRect(canvas!.CanvasSize, slot);
                    return new SkiaSharp.SKPoint(r.MidX, r.MidY);
                }
                void Send(SkiaSharp.Views.Maui.SKTouchAction action, SkiaSharp.SKPoint at) =>
                    typeof(Views.BoxBrowserPage).GetMethod("Touch", flags)!.Invoke(page,
                        [canvas, new SkiaSharp.Views.Maui.SKTouchEventArgs(1, action, at, action != SkiaSharp.Views.Maui.SKTouchAction.Released)]);
                var start = await OnMain(() =>
                {
                    page = TopPage() as Views.BoxBrowserPage ?? throw new InvalidOperationException("not on the storage screen");
                    canvas = (SkiaSharp.Views.Maui.Controls.SKCanvasView)typeof(Views.BoxBrowserPage).GetField("_canvas", flags)!.GetValue(page)!;
                    var a = Center(from);
                    Send(SkiaSharp.Views.Maui.SKTouchAction.Pressed, a);
                    Send(SkiaSharp.Views.Maui.SKTouchAction.Moved, new SkiaSharp.SKPoint(a.X + 30, a.Y + 10));
                    return Services.GetRequiredService<BoxBrowserViewModel>().CarrySource is null ? "no lift" : "lifted";
                });
                if (start != "lifted") return start;
                if (pages != 0)
                {
                    var box = Services.GetRequiredService<BoxBrowserViewModel>();
                    var target = await MainThread.InvokeOnMainThreadAsync(() => box.BoxIndex);
                    await OnMain(() =>
                    {
                        var w = canvas!.CanvasSize.Width;
                        Send(SkiaSharp.Views.Maui.SKTouchAction.Moved, new SkiaSharp.SKPoint(pages > 0 ? w - 2 : 2, canvas.CanvasSize.Height / 2));
                        return "";
                    });
                    var deadline = Stopwatch.GetTimestamp();
                    while (await MainThread.InvokeOnMainThreadAsync(() => box.BoxIndex) != Wrap(target + pages, box.BoxCount))
                    {
                        if (Stopwatch.GetElapsedTime(deadline).TotalSeconds > 10) throw new TimeoutException("edge did not page");
                        await Task.Delay(20);
                    }
                }
                return await OnMain(() =>
                {
                    var end = to >= 0 ? Center(to) : new SkiaSharp.SKPoint(canvas!.CanvasSize.Width / 2, -40);
                    Send(SkiaSharp.Views.Maui.SKTouchAction.Moved, end);
                    Send(SkiaSharp.Views.Maui.SKTouchAction.Released, end);
                    var box = Services.GetRequiredService<BoxBrowserViewModel>();
                    return $"dropped in {box.BoxLabel} slot {box.SelectedSlot}, carrying={box.CarrySource is not null}";
                });

                static int Wrap(int index, int count) => index > count - 1 ? -1 + (index - count) : index < -1 ? count + 1 + index : index;
            }
            case "window":
            {
                // window hide|unhide|minimize|restore|state: macOS's own NSApplication / NSWindow calls.
                var what = Arg(1);
                return await OnMain(() =>
                {
                    var app = ObjC(ObjCClass("NSApplication"), "sharedApplication");
                    var windows = ObjC(app, "windows");
                    var main = ObjC(windows, "firstObject");
                    switch (what)
                    {
                        case "hide": ObjC(app, "hide:", IntPtr.Zero); break;
                        case "unhide": ObjC(app, "unhideWithoutActivation"); break;
                        case "minimize": ObjC(main, "miniaturize:", IntPtr.Zero); break;
                        case "restore": ObjC(main, "deminiaturize:", IntPtr.Zero); break;
                    }
                    var scene = UIKit.UIApplication.SharedApplication.ConnectedScenes.ToArray<UIKit.UIScene>().FirstOrDefault();
                    return $"scene {scene?.ActivationState} · hidden={ObjCBool(app, "isHidden")} minimized={ObjCBool(main, "isMiniaturized")} visible={ObjCBool(main, "isVisible")}";
                });
            }
            case "resize":
            {
                // resize W H (points): the Mac window, as a drag of its corner would; returns once laid out and painted.
                var w = IntArg(1, 1280);
                var h = IntArg(2, 760);
                var started = Stopwatch.GetTimestamp();
                await OnMain(() =>
                {
                    var scene = UIKit.UIApplication.SharedApplication.ConnectedScenes.ToArray<UIKit.UIScene>().OfType<UIKit.UIWindowScene>().First();
                    var frame = scene.EffectiveGeometry.SystemFrame;
                    scene.RequestGeometryUpdate(new UIKit.UIWindowSceneGeometryPreferencesMac(new CoreGraphics.CGRect(frame.X, frame.Y, w, h)), error =>
                        AppLog.Warn("automation", $"resize refused: {error.LocalizedDescription}"));
                    return "";
                });
                var settled = await SettleAsync(5000);
                var size = await OnMain(() => Application.Current?.Windows.FirstOrDefault() is { } win ? $"{win.Width:0}x{win.Height:0}" : "?");
                return $"window {size} after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms";
            }
            case "visibility":
                // visibility [hidden|visible]: simulates the window being covered / uncovered; always
                // reports AppVisibility and each AppKit window's real occlusion state.
                return await OnMain(() =>
                {
                    if (Arg(1) == "hidden") { MacWindowVisibility.Simulated = false; AppVisibility.Set(false); }
                    if (Arg(1) == "visible") { MacWindowVisibility.Simulated = true; AppVisibility.Set(true); }
                    if (Arg(1) == "real") MacWindowVisibility.Simulated = null;
                    var windows = string.Join(", ", MacAppKit.Windows().Select(w =>
                        $"{(MacAppKit.CallBool(w, "isVisible") ? "shown" : "ordered out")}/{((MacAppKit.CallNUInt(w, "occlusionState") & 2) != 0 ? "unoccluded" : "occluded")}"));
                    return $"app visible={AppVisibility.Visible} · windows: {windows}";
                });
            case "canvases":
                // Every drawn view on the visible page and the ones kept below it in the stack:
                // pixel size and backing memory (a raster canvas keeps a full bitmap of itself).
                return await OnMain(() =>
                {
                    var sb = new StringBuilder();
                    double total = 0;
                    var window = Application.Current?.Windows.FirstOrDefault();
                    var pages = window?.Page?.Navigation.NavigationStack.Concat(window.Page.Navigation.ModalStack) ?? [];
                    foreach (var page in pages)
                    {
                        int count = 0; double mb = 0;
                        void Walk(IVisualTreeElement node, bool shown)
                        {
                            var visible = shown && Visible(node);
                            if (node is SkiaSharp.Views.Maui.Controls.SKCanvasView c)
                            {
                                count++;
                                var size = c.CanvasSize;
                                var m = size.Width * size.Height * 4 / 1048576.0;
                                mb += m;
                                if (Arg(1) == "all" || m >= 1)
                                    sb.Append(CultureInfo.InvariantCulture, $"  {page.GetType().Name,-18} {size.Width,6:0}x{size.Height,-6:0} {m,6:0.0} MB {(visible ? "" : "(hidden)")}\n");
                            }
                            else if (node.GetType().Name.Contains("SKGLView", StringComparison.Ordinal)) sb.Append($"  {page.GetType().Name,-18} SKGLView (GPU)\n");
                            foreach (var child in Children(node)) Walk(child, visible);
                        }
                        Walk(page, true);
                        total += mb;
                        sb.Append(CultureInfo.InvariantCulture, $"{page.GetType().Name}: {count} raster canvases, {mb:0.0} MB\n");
                    }
                    sb.Append(CultureInfo.InvariantCulture, $"total {total:0.0} MB · display scale {UIKit.UIScreen.MainScreen.Scale}");
                    return sb.ToString();
                });
            case "menu-save":
                return await OnMain(() => AppDelegate.SaveFromMenu() ? "saving" : "disabled");
            case "pick":
                MacPickers.AutomationPick = Arg(1);
                return $"next file pick → {Arg(1)}";
            case "texts": return await OnMain(() => Filter(Texts(), Arg(1)));
            case "tree": return await OnMain(() => Tree(IntArg(1, 40)));
            case "select":
            {
                // A click on a grid slot: box "party" or 1-based box number, then the 0-based slot.
                var boxArg = Arg(1);
                var slot = IntArg(2, 0);
                return await OnMain(() =>
                {
                    var box = Services.GetRequiredService<BoxBrowserViewModel>();
                    box.BoxIndex = boxArg.Equals("party", StringComparison.OrdinalIgnoreCase) ? -1 : int.Parse(boxArg, CultureInfo.InvariantCulture) - 1;
                    box.SelectSlot(slot);
                    return $"selected {box.BoxLabel} slot {box.SelectedSlot}";
                });
            }
            case "tap": return await OnMain(() => Tap(Arg(1), IntArg(2, 1)));
            case "type": return await OnMain(() => TypeText(Arg(1), Arg(2)));
            case "state": return await OnMain(State);
            case "hints": return await OnMain(Hints);
            case "glyphs":
                // Simulates a controller connecting (pad) or the last one leaving (keyboard).
                return await OnMain(() =>
                {
                    if (Arg(1).Length > 0) InputGlyphs.Update(Arg(1) == "keyboard");
                    return InputGlyphs.Keyboard ? "keyboard" : "pad";
                });
            case "slots": return await OnMain(Slots);
            case "waitfor": return await WaitForAsync(Arg(1), Arg(2), IntArg(3, 15000));
            case "sleep":
                await Task.Delay(IntArg(1, 100));
                return "";
            case "settle": return await SettleAsync(IntArg(1, 5000));
            case "perf":
            {
                var sb = new StringBuilder();
                foreach (var e in Perf.Snapshot())
                    sb.Append(CultureInfo.InvariantCulture, $"{e.AtMs,9:0.0}  {e.Name}{(e.TookMs is { } t ? $"  took {t:0.0}" : "")}\n");
                if (Arg(1) == "clear") Perf.Clear();
                return sb.ToString();
            }
            case "mem":
            {
                if (Arg(1) == "gc") { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
                using var me = Process.GetCurrentProcess();
                return $"managed {GC.GetTotalMemory(false) / 1048576.0:0.0} MB\nrss {me.WorkingSet64 / 1048576.0:0.0} MB\n" +
                       $"gc0 {GC.CollectionCount(0)} gc1 {GC.CollectionCount(1)} gc2 {GC.CollectionCount(2)}";
            }
            case "log":
            {
                var path = Path.Combine(AppPaths.Data, "logs", "pkforge.log");
                if (!File.Exists(path)) return "";
                var lines = File.ReadAllLines(path);
                return string.Join('\n', lines.TakeLast(IntArg(1, 20)));
            }
            case "quit":
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    await MainThread.InvokeOnMainThreadAsync(() => Environment.Exit(0));
                });
                return "bye";
            default:
                return "commands: ping info press pick texts tree tap type state slots waitfor sleep settle perf mem log quit";
        }
    }

    /// <summary>Only the lines containing the filter (case-insensitive); the header line always stays.</summary>
    private static string Filter(string texts, string filter) =>
        filter.Length == 0 ? texts : string.Join('\n', texts.Split('\n').Where((line, i) => i == 0 || line.Contains(filter, StringComparison.OrdinalIgnoreCase)));

    private static IntPtr ObjCClass(string name) => MacAppKit.Class(name);
    private static IntPtr ObjC(IntPtr target, string selector) => MacAppKit.Call(target, selector);
    private static IntPtr ObjC(IntPtr target, string selector, IntPtr argument) => MacAppKit.Call(target, selector, argument);
    private static bool ObjCBool(IntPtr target, string selector) => MacAppKit.CallBool(target, selector);

    // ---- reading the screen -------------------------------------------------------------

    /// <summary>The page in front of the main window: modal on top, else the navigation stack's current page.</summary>
    private static Page? TopPage()
    {
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window?.Page is not { } root) return null;
        if (root.Navigation.ModalStack.LastOrDefault() is { } modal) return Unwrap(modal);
        return Unwrap(root);
    }

    private static Page Unwrap(Page page) => page switch
    {
        NavigationPage nav when nav.CurrentPage is { } current => Unwrap(current),
        FlyoutPage flyout => Unwrap(flyout.Detail),
        _ => page,
    };

    private static string? PadTop() => Services.GetService<GamepadRouter>()?.TopName;

    private static string? TextOf(object node) => node switch
    {
        Label l => l.Text ?? l.FormattedText?.ToString(),
        Button b => b.Text,
        Entry e => $"{e.Text}‹{e.Placeholder}›",
        Editor e => e.Text,
        SearchBar s => $"{s.Text}‹{s.Placeholder}›",
        _ => null,
    };

    private static bool Visible(object node) =>
        node is not VisualElement ve || (ve.IsVisible && ve.Opacity > 0.01);

    private static IEnumerable<IVisualTreeElement> Children(IVisualTreeElement node) =>
        node.GetVisualChildren() ?? [];

    private static bool Tappable(object node) =>
        node is Button or ImageButton
        || (node is View v && v.GestureRecognizers.Any(g => g is TapGestureRecognizer));

    /// <summary>Every visible text in reading order; tappable ones in [brackets].</summary>
    private static string Texts()
    {
        var page = TopPage();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# page {page?.GetType().Name} · pad {PadTop()}\n");
        if (page is null) return sb.ToString();
        void Walk(IVisualTreeElement node, bool inTappable)
        {
            if (!Visible(node)) return;
            var tappable = inTappable || Tappable(node);
            if (TextOf(node) is { Length: > 0 } text)
            {
                var flat = text.Replace('\n', '⏎').Trim();
                if (flat.Length > 0) sb.Append(tappable ? $"[{flat}]\n" : $"{flat}\n");
            }
            foreach (var child in Children(node)) Walk(child, tappable);
        }
        Walk(page, false);
        return sb.ToString();
    }

    private static string Tree(int maxDepth)
    {
        var page = TopPage();
        var sb = new StringBuilder();
        if (page is null) return "no page";
        void Walk(IVisualTreeElement node, int depth)
        {
            if (!Visible(node) || depth > maxDepth) return;
            var name = node.GetType().Name;
            var id = node is Element { AutomationId: { Length: > 0 } aid } ? $"#{aid}" : "";
            var text = TextOf(node) is { Length: > 0 } t ? $" \"{t.Replace('\n', '⏎')}\"" : "";
            var tap = Tappable(node) ? " (tap)" : "";
            var frame = node is VisualElement ve ? $" {ve.Frame.X:0},{ve.Frame.Y:0} {ve.Frame.Width:0}x{ve.Frame.Height:0}" : "";
            sb.Append(' ', depth * 2).Append(name).Append(id).Append(text).Append(tap).Append(frame).Append('\n');
            foreach (var child in Children(node)) Walk(child, depth + 1);
        }
        Walk(page, 0);
        return sb.ToString();
    }

    /// <summary>Taps the n-th visible element whose text contains the query (or the element's tappable ancestor).</summary>
    private static string Tap(string query, int nth)
    {
        var page = TopPage() ?? throw new InvalidOperationException("no page");
        var matches = new List<(IVisualTreeElement Node, IVisualTreeElement? Target)>();
        void Walk(IVisualTreeElement node, IVisualTreeElement? tappableAncestor)
        {
            if (!Visible(node)) return;
            var target = Tappable(node) ? node : tappableAncestor;
            var text = TextOf(node);
            if (text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase))
                matches.Add((node, target));
            foreach (var child in Children(node)) Walk(child, target);
        }
        Walk(page, null);
        if (matches.Count < nth) throw new InvalidOperationException($"no visible text matching '{query}' ({matches.Count} found)");
        var (hit, tapTarget) = matches[nth - 1];
        switch (tapTarget)
        {
            case Button b:
                ((IButtonController)b).SendClicked();
                return $"clicked Button \"{b.Text}\"";
            case ImageButton ib:
                ((IButtonController)ib).SendClicked();
                return "clicked ImageButton";
            case View v when v.GestureRecognizers.OfType<TapGestureRecognizer>().FirstOrDefault() is { } tap:
                FireTap(tap, v);
                return $"tapped {v.GetType().Name} around \"{TextOf(hit)}\"";
            default:
                throw new InvalidOperationException($"'{TextOf(hit)}' has nothing tappable around it");
        }
    }

    private static void FireTap(TapGestureRecognizer tap, View view)
    {
        // SendTapped raises Tapped and runs Command, exactly like a real tap; it is internal.
        var send = typeof(TapGestureRecognizer).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "SendTapped");
        if (send is not null)
        {
            var parameters = send.GetParameters();
            var values = new object?[parameters.Length];
            values[0] = view;
            send.Invoke(tap, values);
            return;
        }
        if (tap.Command?.CanExecute(tap.CommandParameter) == true) tap.Command.Execute(tap.CommandParameter);
        else throw new InvalidOperationException("TapGestureRecognizer.SendTapped not found");
    }

    /// <summary>
    /// Types into the field whose placeholder or text contains <paramref name="match"/> when given;
    /// else the focused field; else the topmost one (overlays such as a picker's search come last).
    /// </summary>
    private static string TypeText(string text, string match)
    {
        var page = TopPage() ?? throw new InvalidOperationException("no page");
        InputView? last = null, focused = null, matched = null;
        void Walk(IVisualTreeElement node)
        {
            if (!Visible(node)) return;
            if (node is InputView input)
            {
                last = input;
                if (input.IsFocused) focused ??= input;
                if (match.Length > 0 && matched is null
                    && ((input.Placeholder ?? "").Contains(match, StringComparison.OrdinalIgnoreCase)
                        || (input.Text ?? "").Contains(match, StringComparison.OrdinalIgnoreCase)))
                    matched = input;
            }
            foreach (var child in Children(node)) Walk(child);
        }
        Walk(page);
        if (match.Length > 0 && matched is null) throw new InvalidOperationException($"no visible text field matching '{match}'");
        var target = matched ?? focused ?? last ?? throw new InvalidOperationException("no visible text field");
        target.Text = text;
        return $"typed into {target.GetType().Name}";
    }

    /// <summary>Every visible footer hint: its button, the key it shows, its label, and whether a click works.</summary>
    private static string Hints()
    {
        var page = TopPage() ?? throw new InvalidOperationException("no page");
        var sb = new StringBuilder();
        void Walk(IVisualTreeElement node)
        {
            if (!Visible(node)) return;
            if (node is Element { AutomationId: { } id } && id.StartsWith("hint:", StringComparison.Ordinal))
            {
                var texts = new List<string>();
                void Collect(IVisualTreeElement n)
                {
                    if (TextOf(n) is { Length: > 0 } t) texts.Add(t);
                    foreach (var c in Children(n)) Collect(c);
                }
                Collect(node);
                var shown = texts.Count > 1 ? texts[0] : "(art)";
                var label = texts.Count > 0 ? texts[^1] : "";
                sb.Append(CultureInfo.InvariantCulture, $"{id[5..],-3} {shown,-7} {label,-24} {(Tappable(node) ? "click" : "NO CLICK")}\n");
                return;
            }
            foreach (var child in Children(node)) Walk(child);
        }
        Walk(page);
        return sb.ToString();
    }

    // ---- app state ----------------------------------------------------------------------

    private static string State()
    {
        var sb = new StringBuilder();
        var window = Application.Current?.Windows.FirstOrDefault();
        var stack = window?.Page?.Navigation.NavigationStack.Select(p => p.GetType().Name) ?? [];
        sb.Append(CultureInfo.InvariantCulture, $"page {TopPage()?.GetType().Name}\npad {PadTop()}\n");
        sb.Append(CultureInfo.InvariantCulture, $"stack {string.Join(" > ", stack)}\nmodals {window?.Page?.Navigation.ModalStack.Count}\n");
        sb.Append(CultureInfo.InvariantCulture, $"windows {Application.Current?.Windows.Count}\n");
        var picker = Services.GetService<SavePickerViewModel>();
        if (picker is not null) sb.Append(CultureInfo.InvariantCulture, $"home busy={picker.IsBusy} status={picker.Status}\n");
        var box = Services.GetService<BoxBrowserViewModel>();
        if (box is not null)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"box connected={box.IsConnected} game={box.ConnectedName} gen={box.ConnectedGeneration} busy={box.IsBusy}\n");
            sb.Append(CultureInfo.InvariantCulture, $"box label={box.BoxLabel} index={box.BoxIndex} selected={box.SelectedSlot}\n");
            sb.Append(CultureInfo.InvariantCulture, $"box status={box.Status}\n");
            if (box.Selected is not null)
                sb.Append(CultureInfo.InvariantCulture,
                    $"edit nick={box.EditNickname} species={box.EditSpecies} lv={box.EditLevel} item={box.EditHeldItem} nature={box.EditNature}\n");
        }
        return sb.ToString();
    }

    private static string Slots()
    {
        var box = Services.GetRequiredService<BoxBrowserViewModel>();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{box.BoxLabel}\n");
        foreach (var s in box.VisibleSlots)
            sb.Append(CultureInfo.InvariantCulture,
                $"{s.Slot,2} {(s.Species is { } sp ? $"#{sp} {s.Nickname}{(s.IsShiny ? " ★" : "")}{(s.HeldItem != 0 ? $" item={s.HeldItem}" : "")}{(s.IsLegal ? "" : " ILLEGAL")}" : "-")}\n");
        return sb.ToString();
    }

    // ---- waiting ------------------------------------------------------------------------

    private static async Task<string> WaitForAsync(string kind, string value, int timeoutMs)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var met = await MainThread.InvokeOnMainThreadAsync(() => kind switch
            {
                "page" => TopPage()?.GetType().Name == value,
                "pad" => PadTop() == value,
                "text" => Texts().Contains(value, StringComparison.OrdinalIgnoreCase),
                "notext" => !Texts().Contains(value, StringComparison.OrdinalIgnoreCase),
                "idle" => !Busy(),
                _ => throw new ArgumentException($"waitfor page|pad|text|notext|idle, not '{kind}'"),
            });
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (met) return $"after {elapsed:0.0} ms";
            if (elapsed > timeoutMs) throw new TimeoutException($"{kind} {value} not met after {timeoutMs} ms");
            await Task.Delay(10);
        }
    }

    private static bool Busy() =>
        Services.GetService<SavePickerViewModel>()?.IsBusy == true || Services.GetService<BoxBrowserViewModel>()?.IsBusy == true;

    /// <summary>Waits until nothing is busy and the main thread answers promptly three times in a row.</summary>
    private static async Task<string> SettleAsync(int timeoutMs)
    {
        var started = Stopwatch.GetTimestamp();
        var quiet = 0;
        while (quiet < 3)
        {
            var ping = Stopwatch.GetTimestamp();
            var busy = await MainThread.InvokeOnMainThreadAsync(Busy);
            var lag = Stopwatch.GetElapsedTime(ping).TotalMilliseconds;
            quiet = !busy && lag < 8 ? quiet + 1 : 0;
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > timeoutMs)
                throw new TimeoutException($"not settled after {timeoutMs} ms");
            await Task.Delay(15);
        }
        return $"after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms";
    }
}
#endif
