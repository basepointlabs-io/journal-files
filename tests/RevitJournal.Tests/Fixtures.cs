namespace RevitJournal.Tests;

/// <summary>
/// Synthetic journal excerpts in the exact line formats seen in real Revit 2025 journals.
/// Names, paths and ids are made up — never paste real journal content here (the repo is public).
/// </summary>
internal static class Fixtures
{
    public const string Header = """
        ' 0:< Initial VM: Avail 134213261 MB, Used 25 MB, Peak 25; RAM: Avail 14789 MB, Used 65 MB, Peak 25
        ' 0:< GUI Resource Usage GDI: Avail 9997, Used 3, User: Used 1
        'C 05-Nov-2026 09:24:38.631;  started recording journal file
        ' Build: 20250515_1515(x64)
        ' Branch: RELEASE_2025.4.2
        ' Release: 2025.4.2
        Dim Jrn
        Set Jrn = CrsJournalScript
        """;

    public const string FailedCloudSync = """
        'C 05-Nov-2026 11:30:31.000;   0:< sync starting
          Jrn.Command "Ribbon"  , "Synchronize the active project with central , ID_FILE_SAVE_TO_CENTRAL"
          ' 1:< SLOG $acbbc001 2026-11-05 11:30:31.586 >STC
          ' 1:< SLOG $acbbc001 2026-11-05 11:30:37.495 >STC:RL:Read
          ' 1:< SLOG $acbbc001 2026-11-05 11:30:38.448 >STC:Save
          ' 1:< RAM Statistics:    11185 /    32212      1769=InUse     7719=Peak
          ' 1:< Time: 11.5.2026 11:30:38:454 Calling: BeginWriteData (Correlation ID = 00000000-0000-0000-0000-000000000000)
          ' 1:<   System.IO.FileNotFoundException: Could not load file or assembly 'System.Security.Cryptography.Algorithms, Version=6.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a'.
          ' 1:< SLOG $acbbc001 2026-11-05 11:30:38.527 <STC:Save
          ' 1:< TaskDialog "Dieser Vorgang konnte nicht abgeschlossen werden. Versuchen Sie es erneut."
          'Id : TaskDialog_Operation_Could_Not_Be_Completed
          'CommonButtons : Close
          ' 1:< SLOG $acbbc001 2026-11-05 11:30:38.572 <STC
        """;

    public const string GoodSync = """
        'C 05-Nov-2026 11:42:24.580;   0:< sync starting
          ' 1:< SLOG $bbbbc002 2026-11-05 11:42:27.297 >STC
          ' 1:< SLOG $bbbbc002 2026-11-05 11:43:19.206 >STC:Save
          ' 1:< SLOG $bbbbc002 2026-11-05 11:43:22.471 <STC:Save
          ' 1:< SLOG $bbbbc002 2026-11-05 11:43:30.007 <STC
        """;

    public const string RoutineLinesThatLookLikeErrors = """
        'generateViewSpecificGRep for RoomElem
        ' 6:< Detect if missing elements for C:\Users\someone\AppData\Local\Model.rvt: File Size: 1000, Element Count: 10
        ' 1:< Unnecessary nesting;F:\Ship\Source\Revit\RevitDB\Family\FamilyDocument.cpp;598;FamilyLoad__UpdateReplicasInSmallDoc;N++EB(NB);
        """;

    public const string ExitRequested = """
          'E 05-Nov-2026 14:56:05.309;   0:<
          Jrn.Command "Internal"  , "Quit the application; prompts to save projects , ID_APP_EXIT"
          ' [Jrn.CloseRevitMainFrame]
        ' 0:< Unregistering all external services.
        """;

    public const string NormalEnd = """
        'C 05-Nov-2026 16:05:02.590;  <-desktop ExitNativeInstance
        'C 05-Nov-2026 16:05:02.592;  finished recording journal file
        """;

    public static string[] Lines(params string[] blocks) =>
        string.Join("\n", blocks).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
}
