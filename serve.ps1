param([int]$Port = 8765)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "Serving $root on http://localhost:$Port/  (data: tijdschrijven.json)"
$mime = @{ ".html"="text/html; charset=utf-8"; ".js"="text/javascript"; ".css"="text/css"; ".json"="application/json"; ".png"="image/png"; ".svg"="image/svg+xml" }
while ($listener.IsListening) {
  try {
    $ctx = $listener.GetContext()
    $method = $ctx.Request.HttpMethod
    $rel = [System.Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath.TrimStart('/'))
    if ([string]::IsNullOrWhiteSpace($rel)) { $rel = "index.html" }

    if ($method -eq "PUT" -or $method -eq "POST") {
      # Alleen het databestand mag geschreven worden.
      if ($rel -eq "tijdschrijven.json") {
        $reader = New-Object System.IO.StreamReader($ctx.Request.InputStream, $ctx.Request.ContentEncoding)
        $body = $reader.ReadToEnd(); $reader.Close()
        $target = Join-Path $root "tijdschrijven.json"
        [System.IO.File]::WriteAllText($target, $body, (New-Object System.Text.UTF8Encoding($false)))
        $ctx.Response.StatusCode = 200
        $ctx.Response.ContentType = "application/json"
        $out = [System.Text.Encoding]::UTF8.GetBytes('{"ok":true}')
        $ctx.Response.OutputStream.Write($out, 0, $out.Length)
      } else {
        $ctx.Response.StatusCode = 403
      }
      $ctx.Response.Close()
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
    $ctx.Response.Close()
  } catch { }
}
