param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$expected = 'C7D27F780DDB6CFFB4730138CD1591E841F4B7EDB155856901CDF5F214394FA1'
$Destination = [IO.Path]::GetFullPath($Destination)
[void][IO.Directory]::CreateDirectory($Destination)
$archive = Join-Path $Destination 'nsis-3.11.zip'
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip?download=1' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'NSIS archive SHA256 mismatch. Not executed.' }
Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force
Join-Path $Destination 'nsis-3.11/makensis.exe'
