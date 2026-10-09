# Definir las rutas base de destino y origen (usando ruta absoluta dinámica)
$pt = Join-Path $env:LOCALAPPDATA "PowerToys"
$desktop = Join-Path $env:USERPROFILE "Desktop"
$build = Join-Path $desktop "Powertoys Patch for Monaco\vo.101.2712\v2 highligter"

# Copiar el archivo ejecutable reemplazando el original
Copy-Item "$build\PyPreviewHost2.exe" "$pt\PowerToys.MonacoPreviewHandler.exe" -Force

# Copiar la librería de dependencias
Copy-Item "$build\ICSharpCode.AvalonEdit.dll" "$pt\ICSharpCode.AvalonEdit.dll" -Force
