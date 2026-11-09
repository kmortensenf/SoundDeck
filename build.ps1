# Builds SoundDeck.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
param([string]$Out = "$PSScriptRoot\SoundDeck.exe")
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$Out" "/win32icon:$PSScriptRoot\assets\SoundDeck.ico" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "$PSScriptRoot\src\*.cs"
