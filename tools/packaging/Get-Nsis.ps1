param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$expected = 'C7D27F780DDB6CFFB4730138CD1591E841F4B7EDB155856901CDF5F214394FA1'
$Destination = [IO.Path]::GetFullPath($Destination)
[void][IO.Directory]::CreateDirectory($Destination)
$archive = Join-Path $Destination 'nsis-3.11.zip'
if (Test-Path -LiteralPath $archive) {
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Cached NSIS archive SHA256 mismatch. Not executed.' }
} else {
    $urls=@(
        # Byte-identical upstream archive, mirrored with COPYING + complete v311 source. Same fixed hash, no replacement tool.
        'https://github.com/zhr2038/UnifiedRGB/releases/download/build-tools-nsis-3.11/nsis-3.11.zip',
        'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip?download=1',
        'https://master.dl.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip?viasf=1',
        'https://pilotfiber.dl.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip',
        'https://netix.dl.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip?download=1',
        'https://psychz.dl.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip?download=1'
    )
    $attempt=0
    foreach($url in $urls) {
        $attempt++
        $download=Join-Path $Destination "nsis-download-$attempt.zip"
        try {
            Invoke-WebRequest $url -OutFile $download -TimeoutSec 30
            $actual=(Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
            if($actual-ne$expected){Write-Warning "NSIS mirror $attempt refused: SHA256=$actual, bytes=$((Get-Item -LiteralPath $download).Length)";continue}
            Copy-Item -LiteralPath $download -Destination $archive
            break
        } catch {Write-Warning "NSIS mirror $attempt unavailable: $($_.Exception.Message)"}
    }
    if(-not(Test-Path -LiteralPath $archive)){throw 'No official NSIS mirror returned the pinned archive. Nothing executed.'}
}
Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force
Join-Path $Destination 'nsis-3.11/makensis.exe'
