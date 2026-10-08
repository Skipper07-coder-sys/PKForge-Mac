using Foundation;
using UIKit;

namespace PKForge.App;

public static class Program
{
    private static void Main(string[] args)
    {
#if MACCATALYST
        // Windows are rebuilt by the app on launch; macOS restoring last session's scenes would
        // hand each one a fresh home page (two main windows, a second screen that is not one).
        NSUserDefaults.StandardUserDefaults.SetBool(false, "NSQuitAlwaysKeepsWindows");
#endif
        UIApplication.Main(args, typeof(PKForgeApplication), typeof(AppDelegate));
    }
}
