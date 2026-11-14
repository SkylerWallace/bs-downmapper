param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version,
    [Parameter(Mandatory)][string]$ChroMapperPath,
    [Parameter(Mandatory)][string]$NativeRanker,
    [Parameter(Mandatory)][string]$Decoder,
    [Parameter(Mandatory)][string]$QuickJsExe,
    [Parameter(Mandatory)][string]$QuickJsLicense,
    [Parameter(Mandatory)][string]$Esbuild,
    [string]$LayoutBundle = (Join-Path $PSScriptRoot 'layouts/Build/bsdownmap-layouts'),
    [string]$Output = (Join-Path $PSScriptRoot '../../dist/BsDownmapper.ChroMapper.dll')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$managed = Join-Path (Resolve-Path -LiteralPath $ChroMapperPath).Path 'ChroMapper_Data/Managed'
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$sdkVersion = (& $dotnet --version).Trim()
$sdk = & $dotnet --list-sdks | Where-Object { $_.StartsWith("$sdkVersion ") } | Select-Object -First 1
if ($sdk -notmatch '\[(.+)\]') { throw "Cannot find .NET SDK $sdkVersion" }
$compiler = Join-Path $Matches[1] "$sdkVersion/Roslyn/bincore/csc.dll"
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$references = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.IO.Compression.dll', 'netstandard.dll',
    'Main.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.AssetBundleModule.dll', 'UnityEngine.UI.dll',
    'UnityEngine.UIModule.dll', 'Unity.InputSystem.dll', 'Unity.TextMeshPro.dll',
    'UnityEngine.IMGUIModule.dll', 'UnityEngine.AudioModule.dll', 'UnityEngine.TextRenderingModule.dll',
    'Input.dll', 'Plugins.dll', 'Newtonsoft.Json.dll', 'UnityEngine.ImageConversionModule.dll'
) | ForEach-Object { '/reference:' + (Join-Path $managed $_) }
$sources = @(
    (Get-ChildItem "$PSScriptRoot/*.cs").FullName
    (Get-ChildItem "$root/engine/*.cs").FullName
    (Get-ChildItem "$root/engine-host/Editor*.cs").FullName
    (Get-ChildItem "$PSScriptRoot/engine-compat/*.cs").FullName
)
$scratch = (New-Item -ItemType Directory -Force (Join-Path $root '.tools/chromapper-build')).FullName
$stage = Join-Path $scratch ([Guid]::NewGuid().ToString('N'))

try {
    $runtime = Join-Path $stage 'runtime'
    $native = Join-Path $runtime '.tools/native-engine'
    $quickJs = Join-Path $runtime '.tools/quickjs-runtime'
    New-Item -ItemType Directory -Path $native,$quickJs -Force | Out-Null
    Copy-Item -LiteralPath $NativeRanker -Destination (Join-Path $native 'shared-percentage-v1-ranker.bdt')
    Copy-Item -LiteralPath $Decoder -Destination (Join-Path $native 'libsndfile_x64.bin')
    Copy-Item -LiteralPath $QuickJsExe -Destination (Join-Path $quickJs 'qjs.exe')
    & $Esbuild (Join-Path $root 'tools/quickjs_dispatch.mjs') --bundle --platform=neutral --format=esm '--external:qjs:std' '--external:fs' --legal-comments=inline "--outfile=$quickJs/dispatch.mjs"
    if ($LASTEXITCODE -ne 0) { throw "QuickJS bundle creation failed with exit code $LASTEXITCODE." }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path $stage 'runtime.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($runtime, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)

    $modules = Join-Path $root '.tools/parity-runtime/node_modules'
    $decoderLicense = Join-Path (Split-Path -Parent $Decoder) 'COPYING'
    $resources = @(
        "/resource:$archive,BsDownmap.runtime.zip,private"
        "/resource:$LayoutBundle,BsDownmap.bsdownmap-layouts,private"
        "/resource:$PSScriptRoot/downmapper.png,BsDownmap.downmapper.png,private"
        "/resource:$decoderLicense,BsDownmap.Licenses.Libsndfile.txt,public"
        "/resource:$QuickJsLicense,BsDownmap.Licenses.QuickJS-NG.txt,public"
        "/resource:$modules/bsmap/LICENSE,BsDownmap.Licenses.BSMap.txt,public"
        "/resource:$modules/valibot/LICENSE.md,BsDownmap.Licenses.Valibot.md,public"
    )
    foreach ($name in 'expert.json','hard.json','normal.json','easy.json') {
        $resources += "/resource:$root/profiles/$name,BsDownmap.$name,private"
    }
    $assemblyInfo = Join-Path $stage 'ReleaseVersion.cs'
    Set-Content -LiteralPath $assemblyInfo -Encoding utf8 -Value @"
[assembly: System.Reflection.AssemblyVersion("$Version.0")]
[assembly: System.Reflection.AssemblyFileVersion("$Version.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("$Version")]
"@
    $sources += $assemblyInfo
    $notices = Join-Path $stage 'THIRD-PARTY-NOTICES.txt'
    $noticeText = @('BS Downmapper bundled dependency notices')
    foreach ($item in @(
        @('libsndfile', $decoderLicense), @('QuickJS-NG', $QuickJsLicense),
        @('BSMap', "$modules/bsmap/LICENSE"), @('Valibot', "$modules/valibot/LICENSE.md")
    )) {
        $noticeText += "`n===== $($item[0]) =====`n" + (Get-Content -LiteralPath $item[1] -Raw)
    }
    Set-Content -LiteralPath $notices -Value ($noticeText -join "`n") -Encoding utf8
    $compiled = Join-Path $stage 'BsDownmapper.ChroMapper.dll'
    & $dotnet $compiler /define:CHROMAPPER /nologo /noconfig /nostdlib+ /target:library /optimize+ /deterministic+ "/out:$compiled" @references @resources @sources
    if ($LASTEXITCODE -ne 0) { throw "Plugin compilation failed with exit code $LASTEXITCODE." }
    New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) -Force | Out-Null
    Move-Item -LiteralPath $compiled -Destination $outputPath -Force
    $outputDirectory = Split-Path -Parent $outputPath
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $outputDirectory 'LICENSE.txt') -Force
    Copy-Item -LiteralPath $notices -Destination (Join-Path $outputDirectory 'THIRD-PARTY-NOTICES.txt') -Force
    $inputs = [ordered]@{}
    foreach ($item in @(
        @('ranker', $NativeRanker), @('decoder', $Decoder), @('quickjs', $QuickJsExe),
        @('layouts', $LayoutBundle), @('chromapperMain', (Join-Path $managed 'Main.dll')),
        @('parityLock', (Join-Path $root 'tools/parity-package-lock.json'))
    )) {
        $inputs[$item[0]] = (Get-FileHash -LiteralPath $item[1] -Algorithm SHA256).Hash
    }
    [ordered]@{
        version = $Version
        dotnetSdk = $sdkVersion
        esbuildVersion = (& $Esbuild --version | Out-String).Trim()
        sha256 = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash
        inputSha256 = $inputs
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputDirectory 'release-manifest.json') -Encoding utf8
    Write-Output $outputPath
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $resolved = (Resolve-Path -LiteralPath $stage).Path
        if ([IO.Path]::GetDirectoryName($resolved) -eq $scratch) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
