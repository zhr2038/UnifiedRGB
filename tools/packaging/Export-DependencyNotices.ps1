param([Parameter(Mandatory)][string]$Artifacts, [Parameter(Mandatory)][string]$Payload, [Parameter(Mandatory)][string]$NsisRoot)
$ErrorActionPreference='Stop'
$packages=@{}
$runtimeVersions=@{}
foreach($runtimeConfig in Get-ChildItem -LiteralPath $Payload -Filter '*.runtimeconfig.json' -File) {
    $config=Get-Content -LiteralPath $runtimeConfig.FullName -Raw|ConvertFrom-Json
    foreach($framework in $config.runtimeOptions.includedFrameworks){$runtimeVersions[$framework.name]=$framework.version}
}
foreach($assetsFile in Get-ChildItem -LiteralPath (Join-Path $Artifacts 'obj') -Filter project.assets.json -File -Recurse) {
    $assets=Get-Content -LiteralPath $assetsFile.FullName -Raw | ConvertFrom-Json -AsHashtable
    foreach($entry in $assets.libraries.GetEnumerator()) {
        if($entry.Value.type -ne 'package'){continue}
        foreach($folder in $assets.packageFolders.Keys) {
            $path=Join-Path $folder $entry.Value.path
            if(Test-Path -LiteralPath $path) {$packages[$entry.Key]=$path;break}
        }
    }
    foreach($folder in $assets.packageFolders.Keys) {
        foreach($framework in $runtimeVersions.GetEnumerator()) {
            $id=$framework.Key.ToLowerInvariant()+'.runtime.win-x64'
            $path=Join-Path $folder "$id/$($framework.Value)"
            if(Test-Path -LiteralPath $path){$packages["$id/$($framework.Value)"]=$path}
        }
    }
}
$records=foreach($entry in $packages.GetEnumerator()|Sort-Object Key) {
    $destination=Join-Path $Payload "Licenses/NuGet/$($entry.Key)"
    [void][IO.Directory]::CreateDirectory($destination)
    $nuspec=Get-ChildItem -LiteralPath $entry.Value -Filter '*.nuspec' -File|Select-Object -First 1
    if(-not$nuspec){throw "Missing source metadata: $($entry.Key)"}
    [xml]$metadata=Get-Content -LiteralPath $nuspec.FullName -Raw
    $license=$metadata.GetElementsByTagName('license')|Select-Object -First 1
    $authors=$metadata.GetElementsByTagName('authors')|Select-Object -First 1
    Copy-Item -LiteralPath $nuspec.FullName -Destination $destination
    $notices=@(Get-ChildItem -LiteralPath $entry.Value -File -Recurse|Where-Object {$_.Name -match '^(LICENSE|COPYING|NOTICE|THIRD.?PARTY)' -and $_.Extension -in @('.txt','.md','.html','')})
    foreach($notice in $notices) {
        $relative=[IO.Path]::GetRelativePath($entry.Value,$notice.FullName)
        $target=Join-Path $destination $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Copy-Item -LiteralPath $notice.FullName -Destination $target
    }
    if($license -and $license.type -eq 'expression' -and $notices.Count -eq 0) {
        # NuGet's SPDX license service is the package's declared license source, not an inferred project license.
        $uri='https://licenses.nuget.org/'+[Uri]::EscapeDataString($license.InnerText)
        Invoke-WebRequest $uri -OutFile (Join-Path $destination 'DECLARED-LICENSE.html')
    }
    if(-not$license -and $notices.Count -eq 0){throw "No license evidence for $($entry.Key)"}
    [ordered]@{Package=$entry.Key;Authors=$authors.InnerText;License=if($license){$license.InnerText}else{'See included notices'};LicenseType=if($license){$license.type}else{'file'};Source='NuGet package metadata';NoticeFiles=$notices.Count}
}
$records|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Payload 'DEPENDENCIES.json') -Encoding utf8
$nsisNotices=Join-Path $Payload 'Licenses/NSIS'
[void][IO.Directory]::CreateDirectory($nsisNotices)
Copy-Item -LiteralPath (Join-Path $NsisRoot 'COPYING') -Destination $nsisNotices
