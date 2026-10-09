# Definir las rutas base de destino y origen
$pt = Join-Path $env:LOCALAPPDATA "PowerToys"
$build = "C:\Users\user\Desktop\Powertoys Patch for Monaco\vo.101.2712\v1 minimal"

# Copiar el archivo ejecutable reemplazando el original
Copy-Item "$build\PyPreviewHost2.exe" "$pt\PowerToys.MonacoPreviewHandler.exe" -Force


