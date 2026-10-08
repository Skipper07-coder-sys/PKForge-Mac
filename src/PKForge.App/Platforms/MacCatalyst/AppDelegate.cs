using Foundation;
using UIKit;

namespace PKForge.App;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        MacSecondaryDisplayHost.ApplyMacDefault();
        var launched = base.FinishedLaunching(application, launchOptions);
        MacPadInput.StartControllers();
        // A key or button held while the app loses focus never sends its release: let go of everything.
        NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.WillResignActiveNotification, _ => { MacPadInput.ReleaseAll(); ForgetKeys(); });
        return launched;
    }

    public override void BuildMenu(IUIMenuBuilder builder)
    {
        base.BuildMenu(builder);
        if (builder.System != UIMenuSystem.MainSystem) return;
        var secondScreen = UIKeyCommand.Create((NSString)"Second Screen", null, ToggleSecondScreenSelector, "2", UIKeyModifierFlags.Command, null);
        builder.InsertChildMenuAtStart(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [secondScreen]), UIMenuIdentifier.Window.GetConstant()!);
        var controls = UICommand.Create("Keyboard & Controller", null, new ObjCRuntime.Selector("pkfShowControls:"), null);
        builder.InsertChildMenuAtStart(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [controls]), UIMenuIdentifier.Help.GetConstant()!);
    }

    private static readonly ObjCRuntime.Selector ToggleSecondScreenSelector = new("pkfToggleSecondScreen:");

    private static MacSecondaryDisplayHost? SecondScreen =>
        IPlatformApplication.Current?.Services.GetService<Domain.ISecondaryDisplayHost>() as MacSecondaryDisplayHost;

    /// <summary>Window ▸ Second Screen (⌘2): Dual ↔ Single, remembered across launches.</summary>
    [Export("pkfToggleSecondScreen:")]
    private void ToggleSecondScreen(NSObject? sender) => SecondScreen?.Toggle();

    /// <summary>Ticks Window ▸ Second Screen while the second window is in use.</summary>
    public override void ValidateCommand(UICommand command)
    {
        base.ValidateCommand(command);
        if (command.Action == ToggleSecondScreenSelector)
            command.State = SecondScreen?.IsAvailable == true ? UIMenuElementState.On : UIMenuElementState.Off;
    }

    [Export("pkfShowControls:")]
    private void ShowControls(NSObject? sender)
    {
        if (MacWindowChrome.MainPresenter() is not { } presenter) return;
        var alert = UIAlertController.Create("Keyboard & Controller",
            "D-pad: arrow keys\n" +
            "A: Return, Space or X\n" +
            "B: Esc, Delete or Z\n" +
            "X: S    Y: A\n" +
            "L: Q    R: W\n" +
            "Start: Tab    Select: right Shift\n\n" +
            "Any Xbox, PlayStation, Switch or MFi controller works as the pad; the left stick moves like the d-pad.\n" +
            "Right-click (or hold a click) does what a long press does on Android.",
            UIAlertControllerStyle.Alert);
        var ok = UIAlertAction.Create("OK", UIAlertActionStyle.Default, null);
        alert.AddAction(ok);
        alert.PreferredAction = ok;
        presenter.PresentViewController(alert, true, () => alert.View?.Window?.MakeKeyWindow());
    }

    // Keys reach the delegate only when no text field took them, so typing a nickname
    // never moves the cursor around the box. ⌘ shortcuts always go to the system.
    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        if (!Route(presses, down: true))
            base.PressesBegan(presses, evt);
    }

    public override void PressesEnded(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        if (!Route(presses, down: false))
            base.PressesEnded(presses, evt);
    }

    public override void PressesCancelled(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        if (!Route(presses, down: false))
            base.PressesCancelled(presses, evt);
    }

    private static readonly HashSet<UIKeyboardHidUsage> KeysDown = [];

    /// <summary>Forgets held keys; paired with <see cref="MacPadInput.ReleaseAll"/> on focus loss.</summary>
    public static void ForgetKeys() => KeysDown.Clear();

    private static bool Route(NSSet<UIPress> presses, bool down)
    {
        // A native sheet (alert, open panel) owns the keyboard; never drive the page beneath it.
        if (down && MacPadInput.NativeSheetShowing()) return false;
        var handled = false;
        foreach (var press in presses)
            handled |= RouteKey(press, down);
        return handled;
    }

    /// <summary>One key edge. Releases ignore modifiers and repeat releases, so the same key-up
    /// can safely arrive twice (see <see cref="PKForgeApplication"/>).</summary>
    public static bool RouteKey(UIPress press, bool down)
    {
        if (press.Key is not { } key) return false;
        if (down && (key.ModifierFlags & (UIKeyModifierFlags.Command | UIKeyModifierFlags.Control | UIKeyModifierFlags.Alternate)) != 0)
            return false;
        if (MacPadInput.Resolve(key.KeyCode) is not { } button) return false;
        if (down)
            return !KeysDown.Add(key.KeyCode) || MacPadInput.Press(button);
        if (KeysDown.Remove(key.KeyCode)) MacPadInput.Release(button);
        return true;
    }
}
