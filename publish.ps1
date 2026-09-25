# Builds Herald as one Herald.exe (framework-dependent: the PC needs the .NET 10 Desktop
# Runtime, https://dotnet.microsoft.com/download/dotnet/10.0 - "Desktop Runtime", x64).
# Output: publish\Herald.exe next to this script.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root "publish"

# SelfContained as an MSBuild property: the --self-contained switch is not always honoured
# when a runtime identifier is given, which silently produces a ~180 MB bundle.
dotnet publish (Join-Path $root "Herald.csproj") -c Release -r win-x64 `
    -p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=none -o $out

$exe = Join-Path $out "Herald.exe"

# Sign with the "Carlos Figueiredo" code-signing certificate if it's in the personal store
# (self-signed, so Windows treats it as unknown until the certificate is trusted on a PC).
# The timestamp keeps the signature valid after the certificate itself expires.
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq "CN=Carlos Figueiredo" } | Select-Object -First 1
if ($cert) {
    $sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com"
    "Signed as $($cert.Subject): $($sig.Status)"
} else {
    "Not signed (no 'CN=Carlos Figueiredo' code-signing certificate in Cert:\CurrentUser\My)."
}

"`nPublished: $exe ({0:N1} MB)" -f ((Get-Item $exe).Length / 1MB)
Get-ChildItem $out | Select-Object Name, @{ n = "Size"; e = { "{0:N0} KB" -f ($_.Length / 1KB) } } | Format-Table -AutoSize
