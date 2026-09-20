[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'LovelyCarDataCapture.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The project needs a release version.' }
$output = Join-Path $repo "artifacts/$version"
New-Item -ItemType Directory -Path $output -Force | Out-Null

& dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
& dotnet build (Join-Path $repo 'tests/LovelyCarDataCapture.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
$testLog = Join-Path $output 'test-results.txt'
& (Join-Path $repo 'tests/bin/Release/net48/LovelyCarDataCapture.Tests.exe') *> $testLog
if ($LASTEXITCODE -ne 0) { throw "Tests failed. See $testLog" }
Get-Content -LiteralPath $testLog -Tail 1 | Write-Output

$dll = Join-Path $repo 'bin/Release/net48/LovelyCarDataCapture.dll'
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
if ($assemblyVersion -ne "$version.0") { throw "Unexpected assembly version $assemblyVersion" }

# Stage inside obj, already excluded from compilation and Git. A unique directory avoids removing
# anything from a previous package attempt and keeps source snapshots out of the normal build glob.
$stage = Join-Path $repo ('obj/package-' + [Guid]::NewGuid().ToString('N'))
$runtimeStage = Join-Path $stage 'runtime'
$sourceStage = Join-Path $stage 'source'
New-Item -ItemType Directory -Path $runtimeStage,$sourceStage -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination $runtimeStage
foreach ($name in @('README.md','LICENSE','CHANGELOG.md')) {
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $runtimeStage
}
Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $runtimeStage -Recurse

# Explicit source allowlist includes uncommitted fixes and regression fixtures, but no local review
# captures, user settings, SimHub dependencies, build output, .git data or previous packages.
$sourceFiles = @('LovelyCarDataCapture.csproj','README.md','LICENSE','CHANGELOG.md','.gitignore') |
    ForEach-Object { Get-Item -LiteralPath (Join-Path $repo $_) }
foreach ($directory in @('src','tests','docs','tools')) {
    $sourceFiles += Get-ChildItem -LiteralPath (Join-Path $repo $directory) -File -Recurse |
        Where-Object { $_.FullName.Substring($repo.Length + 1).Replace('\','/') -notmatch '(^|/)(bin|obj)/' }
}
foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($repo.Length + 1)
    $destination = Join-Path $sourceStage $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}

$runtimeZip = Join-Path $output "LovelyCarDataCapture-$version.zip"
$sourceZip = Join-Path $output "LovelyCarDataCapture-$version-source.zip"
Compress-Archive -Path (Join-Path $runtimeStage '*') -DestinationPath $runtimeZip -Force
Compress-Archive -Path (Join-Path $sourceStage '*') -DestinationPath $sourceZip -Force
Copy-Item -LiteralPath $dll -Destination $output
Copy-Item -LiteralPath (Join-Path $repo 'CHANGELOG.md') -Destination (Join-Path $output 'RELEASE-NOTES.md')

# Record exactly what went into this local source snapshot, including a dirty checkout if present.
$sourceManifest = $sourceFiles | Sort-Object FullName | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' +
        $_.FullName.Substring($repo.Length + 1).Replace('\','/')
}
[IO.File]::WriteAllLines((Join-Path $output 'source-files.sha256'), [string[]]$sourceManifest, [Text.UTF8Encoding]::new($false))
$checksums = @($runtimeZip,$sourceZip,(Join-Path $output 'LovelyCarDataCapture.dll'),
    (Join-Path $output 'RELEASE-NOTES.md'),$testLog,(Join-Path $output 'source-files.sha256')) | ForEach-Object {
        (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_)
    }
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
Write-Output "Packaged $version in $output. Nothing was installed or published."
