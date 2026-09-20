# Publishes aml-mcp as one self-contained file into the plugin, so the plugin
# package carries the server and the user needs no .NET installation.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot "Aml.Editor.Plugin.AmlMcp\runtime"
dotnet publish (Join-Path $root "src\AmlMcp") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -p:BaseOutputPath="$env:TEMP\amlmcp-plugin-build\" -o $out
Write-Host "server: $(Join-Path $out 'aml-mcp.exe')"
