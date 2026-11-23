param(
    [switch]$SelfTest = $true
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectRoot

Write-Host "Building Fontes in $ProjectRoot..." -ForegroundColor Cyan

# Stop any running Fontes instance so Fontes.exe isn't locked
Get-Process -Name "Fontes" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$srcFiles = Get-ChildItem -Path "$ProjectRoot\src\*.cs" | ForEach-Object { $_.FullName }

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Write-Host "Compiling with dotnet build (Fontes.csproj -> net48)..." -ForegroundColor Yellow
    dotnet build "$ProjectRoot\Fontes.csproj" -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
} elseif (Test-Path $csc) {
    Write-Host "Compiling with .NET Framework csc.exe..." -ForegroundColor Yellow
    & $csc /target:winexe /out:"$ProjectRoot\Fontes.exe" /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Xml.dll $srcFiles
    if ($LASTEXITCODE -ne 0) {
        throw "csc.exe build failed with exit code $LASTEXITCODE"
    }
} else {
    throw "Neither dotnet SDK nor csc.exe was found."
}

Write-Host "Build succeeded: $ProjectRoot\Fontes.exe ($((Get-Item "$ProjectRoot\Fontes.exe").Length) bytes)" -ForegroundColor Green

if ($SelfTest) {
    Write-Host "Running Fontes.exe --self-test..." -ForegroundColor Cyan
    $proc = Start-Process -FilePath "$ProjectRoot\Fontes.exe" -ArgumentList "--self-test" -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$ProjectRoot\selftest_stdout.log" -RedirectStandardError "$ProjectRoot\selftest_stderr.log"
    Get-Content "$ProjectRoot\selftest_stdout.log"
    if (Test-Path "$ProjectRoot\selftest_stderr.log") {
        $err = Get-Content "$ProjectRoot\selftest_stderr.log"
        if ($err) { Write-Host $err -ForegroundColor Red }
    }
    Remove-Item "$ProjectRoot\selftest_stdout.log", "$ProjectRoot\selftest_stderr.log" -ErrorAction SilentlyContinue
    if ($proc.ExitCode -ne 0) {
        throw "Fontes --self-test failed with exit code $($proc.ExitCode)"
    }
}
