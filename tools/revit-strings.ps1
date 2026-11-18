# Reads what an installed Revit can say, for building rules that work in every language. Development tool:
# run it on a computer with Revit; the app itself never does this.
#
#   powershell -File tools\revit-strings.ps1 -Revit "C:\Program Files\Autodesk\Revit 2025" -Out C:\Users\me\dev\revit-strings
#
# Writes into -Out two tab-separated files and a folder:
#   dialog-ids.tsv   every "TaskDialog_..." id found in Revit's programs (id, file). The journal logs these ids
#                    ("'Id : TaskDialog_...") and they are the same in every language.
#   strings.tsv      the string tables of the language folders (language, file, string id, text). The same string id
#                    in en-US and de-DE is the same message, so an English text a rule matches can be looked up in
#                    the other languages.
#   baml\            the dialog definitions per language; tools/revit_dialogs.py turns them into
#                    "dialog id -> its texts in every language".
#
# The output is Autodesk's text: keep it out of the repository (samples/ is ignored) and copy into rules only the
# ids and the short phrases a rule needs.
param(
    [string]$Revit = "C:\Program Files\Autodesk\Revit 2025",
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $Out | Out-Null

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

public static class RevitStrings
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int LoadString(IntPtr module, uint id, StringBuilder buffer, int size);

    // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE: resources only, no code is run.
    const uint AsData = 0x00000002 | 0x00000020;

    public static List<string> Table(string file)
    {
        var rows = new List<string>();
        IntPtr module = LoadLibraryEx(file, IntPtr.Zero, AsData);
        if (module == IntPtr.Zero) return rows;
        try
        {
            var buffer = new StringBuilder(8192);
            for (uint id = 0; id < 65536; id++)
            {
                buffer.Length = 0;
                if (LoadString(module, id, buffer, buffer.Capacity) > 0)
                    rows.Add(id + "\t" + buffer.ToString().Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "").Replace("\n", "\\n"));
            }
        }
        finally { FreeLibrary(module); }
        return rows;
    }

    static readonly Regex Ascii = new Regex("TaskDialog_[A-Za-z0-9_]{3,80}", RegexOptions.Compiled);
    static readonly Regex Wide = new Regex("T\0a\0s\0k\0D\0i\0a\0l\0o\0g\0_\0(?:[A-Za-z0-9_]\0){3,80}", RegexOptions.Compiled);

    public static HashSet<string> DialogIds(string file)
    {
        var ids = new HashSet<string>();
        string text = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(file));
        foreach (Match m in Ascii.Matches(text)) ids.Add(m.Value);
        foreach (Match m in Wide.Matches(text)) ids.Add(m.Value.Replace("\0", ""));
        return ids;
    }
}
"@

# ---- dialog ids
$ids = New-Object System.Collections.Generic.List[string]
Get-ChildItem $Revit -File | Where-Object { $_.Extension -in ".dll", ".exe" -and $_.Length -lt 400MB } | ForEach-Object {
    try { foreach ($id in [RevitStrings]::DialogIds($_.FullName)) { $ids.Add("$id`t$($_.Name)") } } catch { }
}
$ids | Sort-Object -Unique | Set-Content -Encoding UTF8 (Join-Path $Out "dialog-ids.tsv")

# ---- string tables per language
$rows = New-Object System.Collections.Generic.List[string]
Get-ChildItem $Revit -Directory | Where-Object { $_.Name -match '^[a-z]{2}-[A-Z]{2}$' } | ForEach-Object {
    $language = $_.Name
    Get-ChildItem $_.FullName -Filter *.dll | ForEach-Object {
        foreach ($row in [RevitStrings]::Table($_.FullName)) { $rows.Add("$language`t$($_.Name)`t$row") }
    }
}
$rows | Set-Content -Encoding UTF8 (Join-Path $Out "strings.tsv")

# ---- dialog definitions (id with its texts) per language, for tools/revit_dialogs.py
# UIFrameworkRes holds them as "taskfiles/gensrc/taskfiles.baml": English in the main file, the rest in the language
# folders. ReflectionOnly: the resources are read, no Revit code is run (and the file's processor type does not matter).
$baml = Join-Path $Out "baml"
New-Item -ItemType Directory -Force -Path $baml | Out-Null
$sources = @(@{ Language = "en-US"; Path = (Join-Path $Revit "UIFrameworkRes.dll") })
Get-ChildItem $Revit -Directory | Where-Object { $_.Name -match '^[a-z]{2}-[A-Z]{2}$' -and $_.Name -ne "en-US" } | ForEach-Object {
    $p = Join-Path $_.FullName "UIFrameworkRes.resources.dll"
    if (Test-Path $p) { $sources += @{ Language = $_.Name; Path = $p } }
}
$definitions = 0
foreach ($source in $sources) {
    $assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($source.Path)
    foreach ($name in $assembly.GetManifestResourceNames() | Where-Object { $_ -like "*.g*.resources" }) {
        $reader = New-Object System.Resources.ResourceReader($assembly.GetManifestResourceStream($name))
        foreach ($entry in $reader) {
            if ($entry.Key -eq "taskfiles/gensrc/taskfiles.baml") {
                $bytes = New-Object byte[] $entry.Value.Length
                [void]$entry.Value.Read($bytes, 0, $bytes.Length)
                [IO.File]::WriteAllBytes((Join-Path $baml "taskfiles.$($source.Language).baml"), $bytes)
                $definitions++
            }
        }
    }
}

"{0} dialog id rows, {1} strings, dialog definitions for {2} languages -> {3}" -f $ids.Count, $rows.Count, $definitions, $Out
