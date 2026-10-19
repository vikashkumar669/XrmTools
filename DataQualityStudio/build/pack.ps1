<#
.SYNOPSIS
  Builds Data Quality Studio in Release, creates the NuGet package and (optionally) pushes it to nuget.org.

.EXAMPLE
  .\build\pack.ps1                         # build + pack only -> .\artifacts\*.nupkg
  .\build\pack.ps1 -Push -ApiKey <key>     # build + pack + push to nuget.org

.NOTES
  Before each release bump the version in BOTH src\DataQualityStudio\DataQualityStudio.csproj (<Version>, <AssemblyVersion>,
  <FileVersion>) and src\DataQualityStudio\DataQualityStudio.nuspec (<version>). Never commit your API key.
#>
param(
    [switch]$Push,
    [string]$ApiKey = $env:NUGET_API_KEY
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\DataQualityStudio\DataQualityStudio.csproj'
$nuspec  = Join-Path $root 'src\DataQualityStudio\DataQualityStudio.nuspec'
$out     = Join-Path $root 'artifacts'

# Version consistency check
[xml]$csproj = Get-Content $project
[xml]$spec   = Get-Content $nuspec
$projVersion = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$specVersion = $spec.package.metadata.version
if ($projVersion -ne $specVersion) {
    throw "Version mismatch: csproj=$projVersion, nuspec=$specVersion. Make them equal before packing."
}

Write-Host "Building Data Quality Studio $specVersion (Release)..." -ForegroundColor Cyan
dotnet build $project -c Release -p:CopyToXrmToolBox=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Write-Host 'Packing...' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $out | Out-Null
dotnet pack $project -c Release --no-build -o $out -p:NuspecFile=$nuspec -p:NuspecBasePath=(Split-Path $project)
if ($LASTEXITCODE -ne 0) { throw 'Pack failed.' }

$package = Join-Path $out ("{0}.{1}.nupkg" -f $spec.package.metadata.id, $specVersion)
Write-Host "Package created: $package" -ForegroundColor Green

if ($Push) {
    if ([string]::IsNullOrWhiteSpace($ApiKey)) { throw 'Pass -ApiKey or set the NUGET_API_KEY environment variable.' }
    Write-Host 'Pushing to nuget.org...' -ForegroundColor Cyan
    dotnet nuget push $package --api-key $ApiKey --source https://api.nuget.org/v3/index.json
    if ($LASTEXITCODE -ne 0) { throw 'Push failed.' }
    Write-Host 'Pushed. nuget.org usually takes 10-60 minutes to validate and index the package.' -ForegroundColor Green
}
