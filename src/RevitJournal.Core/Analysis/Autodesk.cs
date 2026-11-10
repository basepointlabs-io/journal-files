namespace RevitJournal.Core.Analysis;

/// <summary>
/// The Autodesk support articles that generated advice is taken from. Every step shown to the user names the
/// article that recommends it; the rule that carries the step lists the same article in its sources.
/// </summary>
public sealed record AutodeskArticle(string Title, string Url)
{
    private const string Base = "https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/";

    public static readonly AutodeskArticle StoppedWorking = new("\"Revit has stopped working\" and then Revit shuts down",
        Base + "Error-Revit-has-stopped-working-and-then-Revit-2017-shuts-down.html");
    public static readonly AutodeskArticle SplashScreen = new("Revit hangs at the splash screen and doesn't start",
        Base + "When-starting-Revit-splash-screen-is-appeared-and-the-program-stuck.html");
    public static readonly AutodeskArticle NoResponseAtLaunch = new("Autodesk products do not respond when launching from the desktop",
        Base + "Autodesk-products-do-not-respond-when-launching-from-desktop.html");
    public static readonly AutodeskArticle DisableAddIns = new("How to disable add-ins for Revit",
        Base + "How-to-Disable-Add-Ins-for-Revit-Products.html");
    public static readonly AutodeskArticle ClosedUnexpectedly = new("\"A software problem has caused Revit to close unexpectedly\" when launching Revit or opening a project",
        Base + "A-software-problem-has-caused-Revit-Version-to-close-unexpectedly-Error-when-opening-Revit.html");
    public static readonly AutodeskArticle DiskFull = new("\"Disk full while accessing\" or \"…not enough space\" and storage, operation related messages appear in Revit",
        Base + "Disk-full-while-accessing-or-There-is-not-enough-space-on-drive-C-when-syncing-or-saving-in-Revit.html");
    public static readonly AutodeskArticle EventViewer = new("Revit crashes at launch and \"Faulting module: KERNELBASE.dll\" is shown in Event Viewer",
        Base + "Faulting-module-KERNELBASE-dll-appears-in-Event-Viewer-when-Revit-crashes-upon-opening.html");

    public static readonly AutodeskArticle SystemRequirements = new("System requirements for Revit 2025 products",
        Base + "System-requirements-for-Revit-2025-products.html");
    public static readonly AutodeskArticle MemoryRecommendations = new("Memory Recommendations",
        "https://help.autodesk.com/view/RVT/2020/ENU/?guid=GUID-C395AAC8-B5E2-40A5-8B48-1BFEEA9116D6");
    public static readonly AutodeskArticle MemoryInstability = new("Memory Usage and Instability",
        "https://help.autodesk.com/view/RVT/2020/ENU/?guid=GUID-6BE2E3D7-610F-4F17-B3D7-029FBCFA6FC9");

    /// <summary>The step followed by where Autodesk recommends it.</summary>
    public string Cite(string step) => $"{step} [Autodesk: {Title}]";
}
