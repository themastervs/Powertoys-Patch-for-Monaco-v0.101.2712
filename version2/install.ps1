# Definir las rutas base de destino y origen
$pt = Join-Path $env:LOCALAPPDATA "PowerToys"
$build = "C:\Users\user\Desktop\Powertoys Patch for Monaco\vo.101.2712\v2 highligter"

# Copiar el archivo ejecutable reemplazando el original
Copy-Item "$build\PyPreviewHost2.exe" "$pt\PowerToys.MonacoPreviewHandler.exe" -Force

# Copiar la librería de dependencias
Copy-Item "$build\ICSharpCode.AvalonEdit.dll" "$pt\ICSharpCode.AvalonEdit.dll" -Force
