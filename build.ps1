param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$frameworkDirectory = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$compiler = Join-Path $frameworkDirectory "csc.exe"
# WinRT metadata lets the menu bar switch radios and read what is playing.
$winMetadata = Join-Path $env:WINDIR "System32\WinMetadata"

if (-not (Test-Path $compiler)) {
    throw "No se encontró el compilador de .NET Framework 4.x."
}

$outputDirectory = Join-Path $projectRoot "build\$Configuration"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$output = Join-Path $outputDirectory "TaskbarDock.exe"
$sourceDirectory = Join-Path $projectRoot "src\TaskbarDock"
$gacRoot = Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_MSIL"
$uiAutomationClient = Join-Path $gacRoot "UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll"
$uiAutomationTypes = Join-Path $gacRoot "UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll"
$windowsBase = Join-Path $gacRoot "WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll"
$sources = Get-ChildItem -LiteralPath $sourceDirectory -Filter "*.cs" |
    Sort-Object Name |
    Select-Object -ExpandProperty FullName

$compilerArguments = @(
    "/nologo",
    "/target:winexe",
    "/platform:x64",
    "/out:$output",
    "/win32manifest:$(Join-Path $sourceDirectory 'app.manifest')",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:System.Web.Extensions.dll",
    "/reference:System.Management.dll",
    "/reference:$(Join-Path $frameworkDirectory 'System.Runtime.WindowsRuntime.dll')",
    "/reference:$(Join-Path $frameworkDirectory 'System.Runtime.dll')",
    "/reference:$(Join-Path $winMetadata 'Windows.Foundation.winmd')",
    "/reference:$(Join-Path $winMetadata 'Windows.Devices.winmd')",
    "/reference:$(Join-Path $winMetadata 'Windows.Media.winmd')",
    "/reference:$(Join-Path $winMetadata 'Windows.Storage.winmd')",
    "/reference:$uiAutomationClient",
    "/reference:$uiAutomationTypes",
    "/reference:$windowsBase"
)

if ($Configuration -eq "Release") {
    $compilerArguments += "/optimize+"
} else {
    $compilerArguments += @("/debug+", "/define:DEBUG;TRACE")
}

& $compiler $compilerArguments $sources

if ($LASTEXITCODE -ne 0) {
    throw "La compilación terminó con código $LASTEXITCODE."
}

Write-Host "Compilación completada: $output"
