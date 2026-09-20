# Publishes aml-mcp as one self-contained file into the plugin, so the plugin package
# carries the server and nobody needs a .NET installation to run it.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot "Aml.Editor.Plugin.AmlMcp\runtime"

# The repository pins the SDK in global.json. A dotnet on PATH that is too old fails with
# a message about the pin, so fall back to a user-local installation if there is one.
function Find-Dotnet {
    $candidates = @("dotnet", (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"))
    foreach ($candidate in $candidates) {
        try {
            & $candidate --list-sdks 2>$null | Where-Object { $_ -match "^10\." } | Out-Null
            if (& $candidate --list-sdks 2>$null | Where-Object { $_ -match "^10\." }) { return $candidate }
        }
        catch { }
    }
    throw "No .NET 10 SDK found. Install it, or put it in %USERPROFILE%\.dotnet."
}

$dotnet = Find-Dotnet
Write-Host "using $dotnet"

& $dotnet publish (Join-Path $root "src\AmlMcp") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -p:BaseOutputPath="$env:TEMP\amlmcp-plugin-build\" -o $out

Write-Host ""
Write-Host "server:  $(Join-Path $out 'aml-mcp.exe')"
Write-Host "next:    $dotnet build $(Join-Path $PSScriptRoot 'AmlMcp.Editor.sln') -c Release"
