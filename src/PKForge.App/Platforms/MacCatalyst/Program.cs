using Foundation;
using UIKit;

namespace PKForge.App;

public static class Program
{
    private static void Main(string[] args)
    {
        // Windows are rebuilt by the app on launch; macOS restoring last session's scenes would
        // hand each one a fresh home page (two main windows, a second screen that is not one).
        NSUserDefaults.StandardUserDefaults.SetBool(false, "NSQuitAlwaysKeepsWindows");
        UIApplication.Main(args, typeof(PKForgeApplication), typeof(AppDelegate));
    }
}
