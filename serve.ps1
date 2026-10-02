param([int]$Port = 8765)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Add-Type -AssemblyName System.Web.Extensions
$json = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$json.MaxJsonLength = [int]::MaxValue
$utf8 = New-Object System.Text.UTF8Encoding($false)   # UTF-8 zonder BOM
$maxBody = 20MB
$maxBackups = 50
$maxEntryDrop = 5

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "Serving $root on http://localhost:$Port/  (data: tijdschrijven.json)"
$mime = @{ ".html"="text/html; charset=utf-8"; ".js"="text/javascript"; ".css"="text/css"; ".json"="application/json"; ".png"="image/png"; ".svg"="image/svg+xml" }

function Send-Json($ctx, [int]$code, $obj) {
  $bytes = $utf8.GetBytes($json.Serialize($obj))
  $ctx.Response.StatusCode = $code
  $ctx.Response.ContentType = "application/json; charset=utf-8"
  $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
}

# Leest de body (altijd als UTF-8) en geeft $null terug als hij groter is dan $maxBody.
function Read-Body($request) {
  if ($request.ContentLength64 -gt $maxBody) { return $null }
  $ms = New-Object System.IO.MemoryStream
  $buf = New-Object byte[] 81920
  while (($n = $request.InputStream.Read($buf, 0, $buf.Length)) -gt 0) {
    $ms.Write($buf, 0, $n)
    if ($ms.Length -gt $maxBody) { return $null }
  }
  $text = $utf8.GetString($ms.ToArray())
  return $text.TrimStart([char]0xFEFF)
}

# Aantal entries in een JSON-tekst, of $null als het geen geldige data met een entries-array is.
function Get-EntryCount([string]$text) {
  try { $obj = $json.DeserializeObject($text) } catch { return $null }
  if ($obj -isnot [System.Collections.IDictionary]) { return $null }
  if (-not $obj.ContainsKey("entries")) { return $null }
  $e = $obj["entries"]
  if ($e -is [string] -or $e -isnot [System.Collections.IEnumerable]) { return $null }
  return @($e).Count
}

function Save-Data($ctx, [string]$target) {
  $body = Read-Body $ctx.Request
  if ($null -eq $body) {
    Send-Json $ctx 413 @{ ok = $false; error = "Bestand te groot (maximaal 20 MB). Er is niets opgeslagen." }
    return
  }
  $newCount = Get-EntryCount $body
  if ($null -eq $newCount) {
    Send-Json $ctx 400 @{ ok = $false; error = "Ongeldige data: geen geldige JSON met een 'entries'-lijst. Er is niets opgeslagen." }
    return
  }
  $backupDir = Join-Path $root "backups"
  if (Test-Path $target -PathType Leaf) {
    $oldCount = $null
    try { $oldCount = Get-EntryCount ([System.IO.File]::ReadAllText($target, $utf8)) } catch { }
    if ($null -ne $oldCount -and $newCount -lt ($oldCount - $maxEntryDrop)) {
      Send-Json $ctx 409 @{ ok = $false; error = "Opslaan geweigerd: de pagina heeft $newCount regels, op schijf staan er $oldCount. Waarschijnlijk is de pagina verouderd. Herlaad de pagina (F5) en probeer opnieuw." }
      return
    }
    # Back-up van de huidige versie, daarna alleen de oudste automatische back-ups opruimen.
    if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir | Out-Null }
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $backup = Join-Path $backupDir "tijdschrijven_$stamp.json"
    if (-not (Test-Path $backup)) { Copy-Item $target $backup }   # nooit een bestaande back-up overschrijven
    Get-ChildItem $backupDir -Filter "tijdschrijven_????????_??????.json" |
      Sort-Object Name -Descending | Select-Object -Skip $maxBackups |
      ForEach-Object { Remove-Item $_.FullName -Force }
  }
  # Atomair: eerst naar een tijdelijk bestand, dan vervangen.
  $tmp = "$target.tmp"
  [System.IO.File]::WriteAllText($tmp, $body, $utf8)
  if (Test-Path $target -PathType Leaf) { [System.IO.File]::Replace($tmp, $target, [NullString]::Value) }
  else { [System.IO.File]::Move($tmp, $target) }
  Send-Json $ctx 200 @{ ok = $true; entries = $newCount }
}

while ($listener.IsListening) {
  $ctx = $null
  try {
    $ctx = $listener.GetContext()
    $method = $ctx.Request.HttpMethod
    $rel = [System.Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath.TrimStart('/'))
    if ([string]::IsNullOrWhiteSpace($rel)) { $rel = "index.html" }

    if ($method -eq "PUT" -or $method -eq "POST") {
      # Alleen het databestand mag geschreven worden.
      if ($rel -eq "tijdschrijven.json") {
        Save-Data $ctx (Join-Path $root "tijdschrijven.json")
      } else {
        $ctx.Response.StatusCode = 403
      }
      continue
    }

    # GET: statische bestanden
    $path = Join-Path $root $rel
    if (Test-Path $path -PathType Leaf) {
      $bytes = [System.IO.File]::ReadAllBytes($path)
      $ext = [System.IO.Path]::GetExtension($path).ToLower()
      if ($mime.ContainsKey($ext)) { $ctx.Response.ContentType = $mime[$ext] }
      # data niet cachen zodat de app altijd de laatste versie krijgt
      if ($rel -eq "tijdschrijven.json") { $ctx.Response.Headers.Add("Cache-Control", "no-store") }
      $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    } else {
      $ctx.Response.StatusCode = 404
    }
  } catch {
    Write-Host ("FOUT: " + $_.Exception.Message) -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    if ($ctx) {
      try { Send-Json $ctx 500 @{ ok = $false; error = "Serverfout: " + $_.Exception.Message } } catch { }
    }
  } finally {
    if ($ctx) { try { $ctx.Response.Close() } catch { } }
  }
}
