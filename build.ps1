# Compila Cordel sobre .NET Framework 4.8 (incluido en Windows 10 y 11) con el compilador
# Roslyn de Visual Studio 2022: cualquier edición, incluida la gratuita Community, o las Build Tools.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$csc = Get-ChildItem "$env:ProgramFiles\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe",
                     "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe" -ErrorAction SilentlyContinue |
       Select-Object -First 1
if (-not $csc) {
    Write-Host 'No se encuentra el compilador. Instala Visual Studio 2022 Community o las Build Tools de Visual Studio 2022'
    Write-Host 'con la carga de trabajo "Desarrollo de escritorio de .NET": https://visualstudio.microsoft.com/es/downloads/'
    exit 1
}
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$refs = @("$fx\WPF\PresentationCore.dll", "$fx\WPF\PresentationFramework.dll", "$fx\WPF\WindowsBase.dll",
          "$fx\System.Xaml.dll", "$fx\System.Windows.Forms.dll", "$fx\System.Drawing.dll",
          "$fx\Microsoft.VisualBasic.dll", "$fx\System.dll", "$fx\System.Core.dll") | ForEach-Object { "/r:$_" }
$out = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$src = Get-ChildItem $here -Filter *.cs | ForEach-Object { $_.FullName }
$common = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/langversion:latest', '/nowarn:1701,1702',
            "/win32manifest:$here\app.manifest", "/resource:$here\pinza.png,Cordel.pinza.png") + $refs + $src

# Primera pasada sin icono para poder dibujarlo, segunda con el icono incrustado.
& $csc.FullName @common "/out:$out\Cordel.exe"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& "$out\Cordel.exe" --write-icon "$here\cordel.ico" | Out-Null
Start-Sleep -Milliseconds 300
& $csc.FullName @common "/win32icon:$here\cordel.ico" "/out:$out\Cordel.exe"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Write-Host "OK -> $out\Cordel.exe"
