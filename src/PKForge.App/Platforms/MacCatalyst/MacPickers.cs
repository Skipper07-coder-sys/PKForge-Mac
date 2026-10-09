using Foundation;
using PKForge.Domain;
using UIKit;
using UniformTypeIdentifiers;

namespace PKForge.App;

/// <summary>
/// macOS open panels (UIDocumentPicker in open-in-place mode, never "as copy"): the app edits the
/// emulator's own file, not a sandbox duplicate the emulator would never see.
/// </summary>
public sealed class MacPickers : IDocumentPicker, IFolderPicker
{
#if PKF_AUTOMATION
    /// <summary>Test builds: the next file pick answers with this path instead of an open panel.</summary>
    internal static string? AutomationPick;
#endif

    public async ValueTask<PickedDocument?> PickSaveAsync(CancellationToken cancellationToken = default)
    {
#if PKF_AUTOMATION
        if (Interlocked.Exchange(ref AutomationPick, null) is { } scripted)
            return new PickedDocument(scripted, Path.GetFileName(scripted));
#endif
        var urls = await PickAsync([UTTypes.Item], allowMultiple: false, cancellationToken);
        return urls.Count == 0 ? null : ToDocument(urls[0]);
    }

    public async ValueTask<IReadOnlyList<PickedDocument>> PickManyAsync(CancellationToken cancellationToken = default)
    {
        var urls = await PickAsync([UTTypes.Item], allowMultiple: true, cancellationToken);
        return urls.Select(ToDocument).ToList();
    }

    public ValueTask<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken = default) =>
        PickFolderAsync(startIn: null, cancellationToken);

    public ValueTask<PickedFolder?> PickFolderAsync(EmulatorKind kind, CancellationToken cancellationToken = default) =>
        PickFolderAsync(DefaultFolder(kind), cancellationToken);

    /// <summary>Where each emulator keeps its saves on macOS, when that folder exists.</summary>
    public static string? DefaultFolder(EmulatorKind kind)
    {
        var support = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        string[] candidates = kind switch
        {
            EmulatorKind.RetroArch => [Path.Combine(support, "RetroArch", "saves"), Path.Combine(support, "RetroArch")],
            EmulatorKind.OpenEmu => [Path.Combine(support, "OpenEmu", "Battery Saves")],
            EmulatorKind.DeSmuME => [Path.Combine(support, "DeSmuME", "Battery"), Path.Combine(support, "DeSmuME")],
            EmulatorKind.Dolphin => [Path.Combine(support, "Dolphin", "GC"), Path.Combine(support, "Dolphin")],
            EmulatorKind.Azahar => [Path.Combine(support, "Azahar"), Path.Combine(support, "Lime3DS"), Path.Combine(support, "Citra")],
            EmulatorKind.Eden => [Path.Combine(support, "eden"), Path.Combine(support, "Eden")],
            EmulatorKind.MelonDS => [Path.Combine(support, "melonDS")],
            _ => [],
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private async ValueTask<PickedFolder?> PickFolderAsync(string? startIn, CancellationToken cancellationToken)
    {
        var urls = await PickAsync([UTTypes.Folder], allowMultiple: false, cancellationToken, startIn);
        if (urls.Count == 0 || urls[0].Path is not { } path) return null;
        return new PickedFolder(path, Path.GetFileName(path.TrimEnd('/')) is { Length: > 0 } name ? name : path);
    }

    private static PickedDocument ToDocument(NSUrl url)
    {
        var path = url.Path ?? throw new InvalidOperationException("The picked item is not a local file.");
        return new PickedDocument(path, Path.GetFileName(path));
    }

    private static Task<IReadOnlyList<NSUrl>> PickAsync(UTType[] types, bool allowMultiple, CancellationToken cancellationToken,
        string? startIn = null)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<NSUrl>>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
#if MACCATALYST
                var presenter = MacWindowChrome.MainPresenter()
#else
                var presenter = Platform.GetCurrentUIViewController()
#endif
                    ?? throw new InvalidOperationException("No window is available to present the picker.");
                var picker = new UIDocumentPickerViewController(types, asCopy: false)
                {
                    AllowsMultipleSelection = allowMultiple,
                    ShouldShowFileExtensions = true,
                };
                if (startIn is not null)
                    picker.DirectoryUrl = NSUrl.FromFilename(startIn);
                CancellationTokenRegistration registration = default;
                picker.DidPickDocumentAtUrls += (_, e) =>
                {
                    registration.Dispose();
#if IOS
                    // The sandbox lets the app into a pick only while its URL is open.
                    foreach (var url in e.Urls) IosSecurityScope.Grant(url);
#endif
                    completion.TrySetResult(e.Urls);
                };
                picker.WasCancelled += (_, _) => { registration.Dispose(); completion.TrySetResult([]); };
                registration = cancellationToken.Register(() => MainThread.BeginInvokeOnMainThread(() =>
                {
                    picker.DismissViewController(true, null);
                    completion.TrySetCanceled(cancellationToken);
                }));
                presenter.PresentViewController(picker, true, null);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        });
        return completion.Task;
    }
}
