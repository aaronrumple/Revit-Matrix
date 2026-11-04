param(
  [string]$CertificateThumbprint,
  [string]$PfxPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Checked {
  param([string]$File, [string[]]$Arguments)
  & $File @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "$([IO.Path]::GetFileName($File)) failed with exit code $LASTEXITCODE."
  }
}

function Find-WindowsSdkTool {
  param([string]$Name)
  $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
  if (Test-Path $sdkRoot) {
    $versions = Get-ChildItem -LiteralPath $sdkRoot -Directory |
      Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending
    foreach ($version in $versions) {
      $candidate = Join-Path $version.FullName "x64\$Name"
      if (Test-Path $candidate) { return $candidate }
    }
  }
  throw "$Name was not found. Install the Windows SDK (including MSIX packaging/signing tools)."
}

$package = Get-Content -Raw -LiteralPath (Join-Path $root 'package.json') | ConvertFrom-Json
$manifestPath = Join-Path $root 'AppxManifest.xml'
$manifestXml = New-Object System.Xml.XmlDocument
$manifestXml.Load($manifestPath)
$identity = $manifestXml.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
if (-not $identity) { throw 'AppxManifest.xml is missing Package/Identity.' }
$publisher = $identity.GetAttribute('Publisher')
$publisherForMatch = $publisher -replace '^CN="(.*)"$', 'CN=$1'

$baseVersion = ($package.version -split '[-+]')[0]
if ($baseVersion -match '^\d+\.\d+\.\d+$') {
  $msixVersion = "$baseVersion.0"
} elseif ($baseVersion -match '^\d+\.\d+\.\d+\.\d+$') {
  $msixVersion = $baseVersion
} else {
  throw "package.json version '$($package.version)' must have three or four numeric parts."
}
$identity.SetAttribute('Version', $msixVersion)

$buildDir = Join-Path $root 'build'
$publishDir = Join-Path $buildDir 'publish'
$msixDir = Join-Path $buildDir 'msix'
$stageDir = Join-Path $msixDir 'package'
$msixPath = Join-Path $msixDir "TheRevitMatrix-$baseVersion-x64.msix"

if (Test-Path $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
Invoke-Checked 'dotnet' @(
  'publish', 'TheRevitMatrix.csproj', '-c', 'Release', '-r', 'win-x64',
  '--self-contained', 'true', '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true', "-p:Version=$baseVersion",
  '-o', $publishDir
)

if (Test-Path $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir '*') -Destination $stageDir -Recurse -Force
$manifestXml.Save((Join-Path $stageDir 'AppxManifest.xml'))
$assetDir = Join-Path $stageDir 'Assets'
New-Item -ItemType Directory -Path $assetDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Assets\TheRevitMatrix.png') -Destination $assetDir -Force
# Logos at the sizes Windows and the Store ask for (scale-* and targetsize-* variants)
Copy-Item -Path (Join-Path $root 'Assets\Store\*') -Destination $assetDir -Force

$makeAppx = Find-WindowsSdkTool 'makeappx.exe'
$signTool = Find-WindowsSdkTool 'signtool.exe'
$makePri = Find-WindowsSdkTool 'makepri.exe'

# Index the logo variants into resources.pri so the manifest's plain names
# (Assets\Square44x44Logo.png etc.) resolve to the right size on each screen.
$priConfig = Join-Path $msixDir 'priconfig.xml'
Invoke-Checked $makePri @('createconfig', '/cf', $priConfig, '/dq', 'en-US', '/pv', '10.0.0', '/o')
Invoke-Checked $makePri @(
  'new', '/pr', $stageDir, '/cf', $priConfig,
  '/mn', (Join-Path $stageDir 'AppxManifest.xml'),
  '/of', (Join-Path $stageDir 'resources.pri'), '/o'
)
if (Test-Path $msixPath) { Remove-Item -LiteralPath $msixPath -Force }
Invoke-Checked $makeAppx @('pack', '/d', $stageDir, '/p', $msixPath, '/o')

if ($PfxPath) {
  $PfxPath = (Resolve-Path -LiteralPath $PfxPath).Path
  $securePassword = Read-Host 'PFX password' -AsSecureString
  $passwordPtr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
  try {
    $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPtr)
    Invoke-Checked $signTool @('sign', '/fd', 'SHA256', '/f', $PfxPath, '/p', $plainPassword, $msixPath)
  } finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPtr)
    Remove-Variable plainPassword -ErrorAction SilentlyContinue
  }
} else {
  if (-not $CertificateThumbprint) {
    $candidates = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object {
      $_.HasPrivateKey -and ($_.Subject.Replace('\,', ',') -replace '^CN="(.*)"$', 'CN=$1') -eq $publisherForMatch
    })
    if ($candidates.Count -ne 1) {
      $createCertificate = "`$cert = New-SelfSignedCertificate -Type Custom -Subject '$publisher' -KeyUsage DigitalSignature -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')"
      throw "Found $($candidates.Count) current-user signing certificates matching Publisher '$publisher'. The previous Aaron Rumple, AIA certificate does not match this Store identity. For local validation, create a matching development certificate with:`n$createCertificate`nThen run: npm.cmd run msix -- -CertificateThumbprint `$cert.Thumbprint. For production signing, use a certificate whose subject is exactly '$publisher'."
    }
    $CertificateThumbprint = $candidates[0].Thumbprint
  }
  Invoke-Checked $signTool @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint, '/s', 'My', $msixPath)
}

$verificationOutput = & $signTool verify /pa /v $msixPath 2>&1
$verificationExitCode = $LASTEXITCODE
$verificationOutput | ForEach-Object { Write-Host $_ }
if ($verificationExitCode -ne 0) {
  $verificationText = $verificationOutput -join "`n"
  if ($verificationText -match 'terminated in a root certificate which is not trusted by the trust provider') {
    Write-Warning 'The MSIX was signed, but this self-signed certificate is not trusted on this computer. Import its public certificate into Local Computer > Trusted People before installing the package.'
  } else {
    throw "SignTool verification failed with exit code $verificationExitCode."
  }
}
Write-Host "`nSigned MSIX created: $msixPath"
