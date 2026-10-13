param([string]$InnoCompiler = '')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:NUGET_PACKAGES = Join-Path $workspace 'artifacts\packages'
    $env:APPDATA = Join-Path $workspace 'artifacts\build-profile\Roaming'
    $env:LOCALAPPDATA = Join-Path $workspace 'artifacts\build-profile\Local'
    dotnet restore WindowsCleaner.sln --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build WindowsCleaner.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/Cleaner.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $payload = Join-Path $workspace 'artifacts\payload'
    $output = Join-Path $workspace 'artifacts\installer'
    New-Item -ItemType Directory -Force -Path $payload,$output | Out-Null
    foreach ($component in @('Service', 'Tray')) {
        dotnet publish "src/Cleaner.$component/Cleaner.$component.csproj" -c Release --no-restore --self-contained false -p:UseAppHost=false -o "$payload/$component"
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $component" }
    }
    # Redistribute the installed official x64 runtime, avoiding a NuGet/network dependency.
    $dotnetRoot = Split-Path (Get-Command dotnet.exe).Source
    $runtime = Join-Path $payload 'Runtime'
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    Copy-Item -LiteralPath "$dotnetRoot\dotnet.exe" -Destination $runtime -Force
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $version = Get-ChildItem "$dotnetRoot\shared\$framework" -Directory |
            Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if (!$version) { throw "Install the .NET 10 x64 $framework runtime to build the offline installer." }
        $destination = Join-Path $runtime "shared\$framework\$($version.Name)"
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Copy-Item -Path "$($version.FullName)\*" -Destination $destination -Recurse -Force
    }
    $runtimeHost = Get-ChildItem "$dotnetRoot\host\fxr" -Directory | Where-Object { $_.Name -match '^10\.0\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    $hostDestination = Join-Path $runtime "host\fxr\$($runtimeHost.Name)"
    New-Item -ItemType Directory -Force -Path $hostDestination | Out-Null
    Copy-Item -Path "$($runtimeHost.FullName)\*" -Destination $hostDestination -Force
    Copy-Item -LiteralPath "$dotnetRoot\LICENSE.txt","$dotnetRoot\ThirdPartyNotices.txt" -Destination $runtime -Force
    Copy-Item -LiteralPath README.md -Destination $payload -Force
    $compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    $frameworkDirectory = Split-Path $compiler
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$payload\WindowsCleaner.exe" /reference:System.Windows.Forms.dll "$workspace\installer\launcher.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed.' }
    $references = @('System.Windows.Forms.dll','System.Drawing.dll','System.ServiceProcess.dll','Microsoft.CSharp.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll') |
        ForEach-Object { "/reference:$frameworkDirectory\$_" }
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$payload\Uninstall.exe" "/win32manifest:$workspace\installer\admin.manifest" @references "$workspace\installer\setup.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Uninstaller compilation failed.' }
    $payloadArchive = Join-Path $workspace 'artifacts\payload.zip'
    if (Test-Path -LiteralPath $payloadArchive) { Remove-Item -LiteralPath $payloadArchive }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $payloadArchive, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    if ($InnoCompiler) {
        & $InnoCompiler installer/WindowsCleaner.iss
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    } else {
        & $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$output\WindowsCleaner-Setup-1.0.0.exe" "/win32manifest:$workspace\installer\admin.manifest" "/resource:$payloadArchive,WindowsCleaner.Payload.zip" @references "$workspace\installer\setup.cs"
        if ($LASTEXITCODE -ne 0) { throw 'Offline installer compilation failed.' }
    }
    $setup = Join-Path $output 'WindowsCleaner-Setup-1.0.0.exe'
    Get-FileHash -LiteralPath $setup -Algorithm SHA256 | Format-List
    Write-Host "Ready: $setup"
} finally { Pop-Location }
