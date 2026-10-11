# Creates the self-signed certificate used to sign Stremio-XBOX packages for sideloading.
# Run once on the Windows build PC from the repo root:  powershell -ExecutionPolicy Bypass -File scripts\New-DevCert.ps1
# The subject must match Publisher in StremioXbox\Package.appxmanifest.
$subject = "CN=StremioXboxDev"
$password = ConvertTo-SecureString -String "stremioxbox" -Force -AsPlainText
$pfxPath = Join-Path $PSScriptRoot "..\StremioXbox\StremioXbox_TemporaryKey.pfx"
$cerPath = Join-Path $PSScriptRoot "..\StremioXbox\StremioXbox_TemporaryKey.cer"

$cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
    -FriendlyName "Stremio-XBOX dev signing" -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $pfxPath -Password $password | Out-Null
Export-Certificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $cerPath | Out-Null
Write-Host "Created $pfxPath and $cerPath"
