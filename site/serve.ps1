# Serves the site folder on http://localhost:8765/ for previewing (index.html gets the same skeleton the artifact host adds).
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$l = New-Object System.Net.HttpListener
$l.Prefixes.Add("http://localhost:8765/")
$l.Start()
while ($l.IsListening) {
  $ctx = $l.GetContext()
  $path = $ctx.Request.Url.AbsolutePath.TrimStart('/')
  if ($path -eq '') { $path = 'index.html' }
  $file = Join-Path $root $path
  if (Test-Path $file -PathType Leaf) {
    $bytes = [IO.File]::ReadAllBytes($file)
    if ($path -eq 'index.html') {
      $html = '<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover"><style>:root{color-scheme:light}body{margin:0;font:14px system-ui}img{max-width:100%}[hidden]{display:none!important}</style></head><body>' + [Text.Encoding]::UTF8.GetString($bytes) + '</body></html>'
      $bytes = [Text.Encoding]::UTF8.GetBytes($html)
    }
    $ctx.Response.ContentType = if ($path.EndsWith('.js')) { 'text/javascript; charset=utf-8' } else { 'text/html; charset=utf-8' }
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
  } else { $ctx.Response.StatusCode = 404 }
  $ctx.Response.Close()
}
