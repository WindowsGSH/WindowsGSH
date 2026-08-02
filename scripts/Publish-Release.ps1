[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts",
    [switch]$SkipRestore,
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

function Test-PathWithin {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ParentPath,
        [Parameter(Mandatory = $true)]
        [string]$CandidatePath
    )

    $parent = [System.IO.Path]::GetFullPath($ParentPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $candidate = [System.IO.Path]::GetFullPath($CandidatePath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $parentWithSeparator = $parent + [System.IO.Path]::DirectorySeparatorChar

    return $candidate.Equals($parent, [System.StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartsWith($parentWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)
}

$normalizedVersion = $Version.Trim().TrimStart("v")
if ($normalizedVersion -notmatch "^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$") {
    throw "Version '$Version' must use semantic version format, for example 1.2.3 or 1.2.3-preview.1."
}

$outputRootPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
if (-not (Test-PathWithin -ParentPath $repositoryRoot.Path -CandidatePath $outputRootPath)) {
    throw "OutputRoot must stay inside the repository."
}

$artifactName = "WindowsGSH-v$normalizedVersion-$Runtime"
$publishPath = Join-Path $outputRootPath "publish\$artifactName"
$zipPath = Join-Path $outputRootPath "$artifactName.zip"
$checksumPath = "$zipPath.sha256"

if (-not $SkipRestore) {
    dotnet restore (Join-Path $repositoryRoot "WindowsGSH.sln")
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

    dotnet restore (Join-Path $repositoryRoot "WindowsGSH.csproj") --runtime $Runtime
    if ($LASTEXITCODE -ne 0) { throw "runtime-specific dotnet restore failed." }
}

if (-not $SkipBuild) {
    dotnet build (Join-Path $repositoryRoot "WindowsGSH.sln") `
        --configuration $Configuration `
        --no-restore `
        --disable-build-servers `
        -m:1 `
        -p:Version=$normalizedVersion `
        -p:UseSharedCompilation=false `
        -p:GenerateMSBuildEditorConfigFile=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
}

if (-not $SkipTests) {
    dotnet test (Join-Path $repositoryRoot "WindowsGSH.sln") `
        --configuration $Configuration `
        --no-build
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null
if (Test-Path -LiteralPath $publishPath) {
    $resolvedPublishPath = Resolve-Path -LiteralPath $publishPath
    if (-not (Test-PathWithin -ParentPath $outputRootPath -CandidatePath $resolvedPublishPath.Path)) {
        throw "Refusing to remove publish path outside the artifact directory."
    }
    Remove-Item -LiteralPath $resolvedPublishPath.Path -Recurse -Force
}
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $checksumPath -Force -ErrorAction SilentlyContinue

dotnet publish (Join-Path $repositoryRoot "WindowsGSH.csproj") `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --no-restore `
    --disable-build-servers `
    -m:1 `
    -p:Version=$normalizedVersion `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:UseSharedCompilation=false `
    -p:GenerateMSBuildEditorConfigFile=false `
    --output $publishPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Compress-Archive -Path (Join-Path $publishPath "*") -DestinationPath $zipPath -Force
$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
"$($hash.Hash)  $(Split-Path $zipPath -Leaf)" | Out-File -LiteralPath $checksumPath -Encoding ascii

[pscustomobject]@{
    Version = $normalizedVersion
    Runtime = $Runtime
    PublishPath = $publishPath
    ZipPath = $zipPath
    ChecksumPath = $checksumPath
    SHA256 = $hash.Hash
}
