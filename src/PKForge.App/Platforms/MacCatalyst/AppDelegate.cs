using Foundation;
using UIKit;

namespace PKForge.App;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
#if MACCATALYST
        MacSecondaryDisplayHost.ApplyMacDefault();
#else
        // iPhone: reopen saves picked outside the app before anything reads them.
        IosSecurityScope.RestoreAll();
#endif
        var launched = base.FinishedLaunching(application, launchOptions);
        PKForge.App.Services.Perf.Mark("FinishedLaunching");
#if PKF_AUTOMATION
        Automation.StartIfRequested();
#endif
        MacPadInput.StartControllers();
        // A key or button held while the app loses focus never sends its release: let go of everything.
        NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.WillResignActiveNotification, _ => { MacPadInput.ReleaseAll(); ForgetKeys(); });
        return launched;
    }

#if MACCATALYST
    // The menu bar, the second screen and the controls sheet are Mac-only; the iPhone build shares the rest.
    public override void BuildMenu(IUIMenuBuilder builder)
    {
        base.BuildMenu(builder);
        if (builder.System != UIMenuSystem.MainSystem) return;
        var secondScreen = UIKeyCommand.Create((NSString)"Second Screen", null, ToggleSecondScreenSelector, "2", UIKeyModifierFlags.Command, null);
        builder.InsertChildMenuAtStart(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [secondScreen]), UIMenuIdentifier.Window.GetConstant()!);
        // File ▸ Save Changes (⌘S): the editor's Save changes button, the Mac's usual save key.
        var save = UIKeyCommand.Create((NSString)"Save Changes", null, SaveChangesSelector, "s", UIKeyModifierFlags.Command, null);
        builder.InsertChildMenuAtStart(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [save]), UIMenuIdentifier.File.GetConstant()!);
        var controls = UICommand.Create("Keyboard & Controller", null, new ObjCRuntime.Selector("pkfShowControls:"), null);
        builder.InsertChildMenuAtStart(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [controls]), UIMenuIdentifier.Help.GetConstant()!);
    }

    private static readonly ObjCRuntime.Selector ToggleSecondScreenSelector = new("pkfToggleSecondScreen:");
    private static readonly ObjCRuntime.Selector SaveChangesSelector = new("pkfSaveChanges:");

    /// <summary>The storage screen's view model while that screen has the input (no menu over it) and a Pokémon is selected.</summary>
    internal static ViewModels.BoxBrowserViewModel? EditorInFront()
    {
        var services = IPlatformApplication.Current?.Services;
        if (services?.GetService<PKForge.App.Services.GamepadRouter>()?.TopName != nameof(Views.BoxBrowserPage)) return null;
        var box = services.GetService<ViewModels.BoxBrowserViewModel>();
        return box is { Selected: not null } && box.SaveEditCommand.CanExecute(null) ? box : null;
    }

    [Export("pkfSaveChanges:")]
    private void SaveChanges(NSObject? sender) => SaveFromMenu();

    internal static bool SaveFromMenu()
    {
        if (EditorInFront() is not { } box) return false;
        box.SaveEditCommand.Execute(null);
        return true;
    }

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
        if (command.Action == SaveChangesSelector)
            command.Attributes = EditorInFront() is null ? UIMenuElementAttributes.Disabled : 0;
    }

    [Export("pkfShowControls:")]
    private void ShowControls(NSObject? sender)
    {
        if (MacWindowChrome.MainPresenter() is not { } presenter) return;
        var alert = UIAlertController.Create("Keyboard & Controller",
            "Press what the hints show. Without a controller they name the keys.\n\n" +
            "Move: arrow keys\n" +
            "A (confirm): Return or Space    B (back): Esc or Delete\n" +
            "X, Y: the X and Y keys    L, R: L and R, or Page Up / Page Down\n" +
            "+ (menu): + or Tab    − (select): −\n" +
            "⌘S: save the edited Pokémon\n\n" +
            "Mouse: click a Pokémon to select it, click it again to pick it up, click a slot to put it down. " +
            "Or drag it: let go on a slot to move or swap it, hold it at the box's left or right edge to change boxes, let go outside to put it back. " +
            "Right-click a Pokémon for its menu. Scroll or swipe sideways over a box to change boxes.\n\n" +
            "Any Xbox, PlayStation, Switch or MFi controller works as the pad; the left stick moves like the d-pad.",
            UIAlertControllerStyle.Alert);
        var ok = UIAlertAction.Create("OK", UIAlertActionStyle.Default, null);
        alert.AddAction(ok);
        alert.PreferredAction = ok;
        presenter.PresentViewController(alert, true, () => alert.View?.Window?.MakeKeyWindow());
    }
#endif

    // On the Mac key presses never pass through PKForgeApplication, and a text field takes Esc
    // for itself (it clears). While a picker's search box has the cursor this command takes Esc
    // first, with priority over that, so Esc closes the picker as it does everywhere else.
    private static readonly ObjCRuntime.Selector PickerEscapeSelector = new("pkfPickerEscape:");

    public override UIKeyCommand[] KeyCommands
    {
        get
        {
            var escape = UIKeyCommand.Create(UIKeyCommand.Escape, 0, PickerEscapeSelector);
            escape.WantsPriorityOverSystemBehavior = true;
            return [escape];
        }
    }

    public override bool CanPerform(ObjCRuntime.Selector action, NSObject? withSender) =>
        action == PickerEscapeSelector ? PKForgeApplication.PickerSearchFocused() : base.CanPerform(action, withSender);

    [Export("pkfPickerEscape:")]
    private void PickerEscape(NSObject? sender)
    {
        MacPadInput.Press(PKForge.App.Services.PadButton.B);
        MacPadInput.Release(PKForge.App.Services.PadButton.B);
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

    /// <summary>Held keys and the button each pressed: a release lets go of that button even when
    /// its modifiers (and so its character) changed in between.</summary>
    private static readonly Dictionary<UIKeyboardHidUsage, PKForge.App.Services.PadButton> KeysDown = [];

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
        if (!down)
        {
            if (KeysDown.Remove(key.KeyCode, out var held)) { MacPadInput.Release(held); return true; }
            return MacPadInput.Resolve(key.KeyCode, key.CharactersIgnoringModifiers) is not null;
        }
        if ((key.ModifierFlags & (UIKeyModifierFlags.Command | UIKeyModifierFlags.Control | UIKeyModifierFlags.Alternate)) != 0)
            return false;
        if (MacPadInput.Resolve(key.KeyCode, key.CharactersIgnoringModifiers) is not { } button) return false;
        if (KeysDown.ContainsKey(key.KeyCode)) return true; // key repeat: the pad repeats on its own
        KeysDown[key.KeyCode] = button;
        return MacPadInput.Press(button);
    }
}
