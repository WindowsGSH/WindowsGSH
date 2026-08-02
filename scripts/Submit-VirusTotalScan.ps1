[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath,

    [Parameter(Mandatory = $true)]
    [string]$ApiKey,

    [ValidateRange(15, 300)]
    [int]$PollIntervalSeconds = 30,

    [ValidateRange(1, 60)]
    [int]$TimeoutMinutes = 15
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedFile = Resolve-Path -LiteralPath $FilePath -ErrorAction Stop
$file = Get-Item -LiteralPath $resolvedFile.Path
if (-not $file.Exists) {
    throw "VirusTotal upload file was not found."
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw "VIRUSTOTAL_API_KEY is not configured."
}

$smallUploadLimit = 32MB
$maximumUploadSize = 650MB
if ($file.Length -gt $maximumUploadSize) {
    throw "VirusTotal does not accept files larger than 650 MB."
}

$headers = @{
    "x-apikey" = $ApiKey
    "Accept" = "application/json"
}

$uploadUri = "https://www.virustotal.com/api/v3/files"
if ($file.Length -gt $smallUploadLimit) {
    Write-Host "Requesting a VirusTotal large-file upload URL for $($file.Name)."
    $uploadUrlResponse = Invoke-RestMethod `
        -Method Get `
        -Uri "https://www.virustotal.com/api/v3/files/upload_url" `
        -Headers $headers
    $uploadUri = [string]$uploadUrlResponse.data
    if ([string]::IsNullOrWhiteSpace($uploadUri)) {
        throw "VirusTotal did not return a large-file upload URL."
    }
}

Write-Host "Uploading $($file.Name) to VirusTotal for analysis."
$httpClient = [System.Net.Http.HttpClient]::new()
$multipart = [System.Net.Http.MultipartFormDataContent]::new()
$fileStream = $null
$fileContent = $null
$response = $null
try {
    $httpClient.Timeout = [TimeSpan]::FromMinutes($TimeoutMinutes)
    $httpClient.DefaultRequestHeaders.Add("x-apikey", $ApiKey)
    $httpClient.DefaultRequestHeaders.Accept.Add(
        [System.Net.Http.Headers.MediaTypeWithQualityHeaderValue]::new("application/json"))

    # Invoke-RestMethod -Form produced a malformed multipart body for the large-file upload URL
    # on GitHub's Windows runner. Build the RFC-compliant multipart stream explicitly so the ZIP
    # remains streamed from disk and VirusTotal receives one binary part named exactly "file".
    $fileStream = [System.IO.File]::OpenRead($file.FullName)
    $fileContent = [System.Net.Http.StreamContent]::new($fileStream)
    $fileContent.Headers.ContentType =
        [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/octet-stream")
    $safeUploadName = [System.Text.RegularExpressions.Regex]::Replace(
        $file.Name, "[^A-Za-z0-9._-]", "_")
    $contentDisposition =
        [System.Net.Http.Headers.ContentDispositionHeaderValue]::new("form-data")
    $contentDisposition.Name = '"file"'
    $contentDisposition.FileName = '"' + $safeUploadName + '"'
    # Do not use MultipartFormDataContent.Add(content, name, fileName). .NET adds a filename*
    # parameter that VirusTotal's large-file upload backend rejects as malformed multipart data.
    $fileContent.Headers.ContentDisposition = $contentDisposition
    $multipart.Add($fileContent)

    $response = $httpClient.PostAsync($uploadUri, $multipart).GetAwaiter().GetResult()
    $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        $safeError = $responseBody.Replace($ApiKey, "***", [StringComparison]::Ordinal)
        $safeError = [System.Text.RegularExpressions.Regex]::Replace($safeError, "\s+", " ").Trim()
        if ($safeError.Length -gt 1000) {
            $safeError = $safeError.Substring(0, 1000) + "..."
        }
        $detail = if ([string]::IsNullOrWhiteSpace($safeError)) { "" } else { " Response: $safeError" }
        throw "VirusTotal upload failed with HTTP $([int]$response.StatusCode) ($($response.ReasonPhrase)).$detail"
    }

    $uploadResponse = $responseBody | ConvertFrom-Json -ErrorAction Stop
}
finally {
    # MultipartFormDataContent owns fileContent, which owns fileStream. Disposing the container
    # closes the entire chain even when upload or response parsing fails.
    if ($null -ne $response) {
        $response.Dispose()
    }
    $multipart.Dispose()
    $httpClient.Dispose()
}

$analysisId = [string]$uploadResponse.data.id
if ([string]::IsNullOrWhiteSpace($analysisId)) {
    throw "VirusTotal did not return an analysis identifier."
}

$analysisUri = "https://www.virustotal.com/api/v3/analyses/$analysisId"
$deadline = [System.Diagnostics.Stopwatch]::StartNew()
$timeout = [TimeSpan]::FromMinutes($TimeoutMinutes)
$analysis = $null

while ($deadline.Elapsed -lt $timeout) {
    # The public API allows four requests per minute. Thirty-second polling leaves headroom for
    # the upload request (and the additional upload-URL request required for files over 32 MB).
    Start-Sleep -Seconds $PollIntervalSeconds

    try {
        $analysis = Invoke-RestMethod -Method Get -Uri $analysisUri -Headers $headers
    }
    catch {
        $statusCode = $null
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }

        if ($statusCode -eq 429) {
            Write-Warning "VirusTotal rate limit reached; waiting 60 seconds before retrying."
            Start-Sleep -Seconds 60
            continue
        }

        throw
    }

    $status = [string]$analysis.data.attributes.status
    Write-Host "VirusTotal analysis status: $status"
    if ($status -eq "completed") {
        break
    }
}

if ($null -eq $analysis -or [string]$analysis.data.attributes.status -ne "completed") {
    throw "VirusTotal analysis did not complete within $TimeoutMinutes minute(s)."
}

$stats = $analysis.data.attributes.stats
$sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$totalEngines = 0
foreach ($property in $stats.PSObject.Properties) {
    $totalEngines += [int]$property.Value
}

[pscustomobject]@{
    AnalysisId = $analysisId
    Sha256 = $sha256
    ReportUrl = "https://www.virustotal.com/gui/file/$sha256"
    Malicious = [int]$stats.malicious
    Suspicious = [int]$stats.suspicious
    Harmless = [int]$stats.harmless
    Undetected = [int]$stats.undetected
    TotalEngines = $totalEngines
}
