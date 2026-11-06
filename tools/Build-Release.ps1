param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet build CLPR.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
dotnet run --project tests/Clpr.Tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
$desktopOutput = "artifacts/build-desktop-$Version"
$cliOutput = "artifacts/build-cli-$Version"
dotnet publish src/Clpr.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $desktopOutput
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
dotnet publish src/Clpr.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o $cliOutput
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }
$package = "artifacts/CLPR-$Version-Windows-x64"
if (Test-Path -LiteralPath $package) { throw 'Package directory exists; use a clean checkout or a new version.' }
New-Item -ItemType Directory -Path "$package/CLI" -Force | Out-Null
Copy-Item -LiteralPath "$desktopOutput/Clpr.Desktop.exe" -Destination $package
Copy-Item -LiteralPath "$cliOutput/Clpr.Cli.exe" -Destination "$package/CLI"
Copy-Item -LiteralPath README.md,LICENSE,CHANGELOG.md -Destination $package
Copy-Item -LiteralPath docs/USER_GUIDE.md -Destination "$package/使用指南.md"
$checksums = @("$package/Clpr.Desktop.exe", "$package/CLI/Clpr.Cli.exe") | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    $relative = [IO.Path]::GetRelativePath((Resolve-Path $package).Path, $hash.Path).Replace('\','/')
    "$($hash.Hash)  $relative"
}
$checksums | Set-Content -LiteralPath "$package/SHA256SUMS.txt" -Encoding utf8
$zip = "artifacts/CLPR-$Version-Windows-x64.zip"
Compress-Archive -Path "$package/*" -DestinationPath $zip -Force
$zipHash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
"$($zipHash.Hash)  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath "artifacts/CLPR-$Version-SHA256SUMS.txt" -Encoding utf8
