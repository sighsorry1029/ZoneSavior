param(
    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string] $GameManaged = '',
    [string] $MonoRuntimeDirectory = '',
    [string] $BepInExCore = '',
    [string] $ModDll = "$PSScriptRoot\..\bin\ZoneSavior\Debug\ZoneSavior.dll",
    [Parameter(Mandatory = $true)] [string] $UpgradeWorldDll,
    [Parameter(Mandatory = $true)] [string] $FreshWorldDll
)
$ErrorActionPreference = 'Stop'
$managed = if ($GameManaged) { $GameManaged } else { Join-Path $GamePath 'valheim_Data\Managed' }
$core = if ($BepInExCore) { $BepInExCore } else { Join-Path $GamePath 'BepInEx\core' }
$runtime = if ($MonoRuntimeDirectory) { $MonoRuntimeDirectory } else { Join-Path $GamePath 'MonoBleedingEdge\EmbedRuntime' }
$config = Join-Path (Split-Path $runtime -Parent) 'etc'
foreach ($path in @($ModDll, $UpgradeWorldDll, $FreshWorldDll, (Join-Path $managed 'assembly_valheim.dll'), (Join-Path $runtime 'mono-2.0-bdwgc.dll'))) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required input not found: $path" }
}
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sdk = @(& dotnet --list-sdks | Select-Object -Last 1)[0]
if ($sdk -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A .NET SDK is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('ZoneSavior-EmptyChunkChecks-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$hostExe = Join-Path $directory 'CompatibilityMonoHost.exe'
$probe = Join-Path $directory 'EmptyChunkProbe.dll'
$references = @('mscorlib.dll','System.dll','System.Core.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
& dotnet $compiler /nologo /noconfig /nostdlib+ /langversion:latest @references /target:exe /platform:x64 "/out:$hostExe" "$PSScriptRoot/CompatibilityMonoHost.cs"
if ($LASTEXITCODE) { throw 'Mono host compilation failed.' }
$gameReferences = @('assembly_valheim.dll','assembly_utils.dll','assembly_guiutils.dll','UnityEngine.CoreModule.dll','netstandard.dll') | ForEach-Object { '/reference:' + (Join-Path $managed $_) }
$libraryReferences = @('0Harmony.dll','BepInEx.dll','Mono.Cecil.dll') | ForEach-Object { '/reference:' + (Join-Path $core $_) }
$sources = Get-ChildItem -LiteralPath "$PSScriptRoot/empty-chunks" -Filter '*.cs' | Select-Object -ExpandProperty FullName
& dotnet $compiler /nologo /noconfig /nostdlib+ /langversion:latest /nullable:enable @references @gameReferences @libraryReferences /target:library "/out:$probe" @sources
if ($LASTEXITCODE) { throw 'Empty-chunk probe compilation failed.' }
# Copy final plugin inputs and dependencies only into this disposable test folder.
$isolatedMod = Join-Path $directory 'ZoneSavior.dll'
$isolatedUpgrade = Join-Path $directory 'UpgradeWorld.dll'
$isolatedFresh = Join-Path $directory 'FreshWorld.dll'
Copy-Item -LiteralPath $ModDll -Destination $isolatedMod
Copy-Item -LiteralPath $UpgradeWorldDll -Destination $isolatedUpgrade
Copy-Item -LiteralPath $FreshWorldDll -Destination $isolatedFresh
$freshCore = Join-Path (Split-Path (Resolve-Path -LiteralPath $FreshWorldDll).Path -Parent) 'FreshWorld.Core.dll'
if (Test-Path -LiteralPath $freshCore) { Copy-Item -LiteralPath $freshCore -Destination $directory }
Get-ChildItem -LiteralPath $core -Filter '*.dll' | Copy-Item -Destination $directory
$stdout = Join-Path $directory 'stdout.txt'
$stderr = Join-Path $directory 'stderr.txt'
$arguments = @($runtime,$managed,$config,$probe,$isolatedMod) | ForEach-Object { '"' + $_ + '"' }
$oldUpgrade = [Environment]::GetEnvironmentVariable('ZONESAVIOR_EMPTY_UPGRADE_DLL')
$oldFresh = [Environment]::GetEnvironmentVariable('ZONESAVIOR_EMPTY_FRESH_DLL')
$process = $null
try {
    [Environment]::SetEnvironmentVariable('ZONESAVIOR_EMPTY_UPGRADE_DLL', $isolatedUpgrade)
    [Environment]::SetEnvironmentVariable('ZONESAVIOR_EMPTY_FRESH_DLL', $isolatedFresh)
    $process = Start-Process -FilePath $hostExe -ArgumentList $arguments -WorkingDirectory $directory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $process.Handle
    if (!$process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit(); throw 'Empty-chunk Mono probe timed out.' }
    Get-Content -LiteralPath $stdout,$stderr
    if ($process.ExitCode) { throw "Empty-chunk probe failed with exit code $($process.ExitCode)." }
}
finally {
    [Environment]::SetEnvironmentVariable('ZONESAVIOR_EMPTY_UPGRADE_DLL', $oldUpgrade)
    [Environment]::SetEnvironmentVariable('ZONESAVIOR_EMPTY_FRESH_DLL', $oldFresh)
    if ($process) {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
    Write-Host "Test artifacts retained: $directory"
}
