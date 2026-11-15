<#
.SYNOPSIS
  Proves SacCheck.ps1 against files whose Smart App Control fate is KNOWN, and
  proves each gate separately. Run after any change to SacCheck.ps1.

  Ground truth:
    - the game's own signed DLLs (Unity, Valve) load on SAC machines -> ALLOW
    - CMM_Decoder.dll unsigned was blocked on a user's SAC machine   -> BLOCK
  Constructed negatives (each must fail on the RIGHT check):
    - a signed DLL with one byte changed                             -> intact
    - CMM_Decoder signed with a self-made cert, no timestamp          -> root + timestamp
    - a real signed DLL judged against an AuthRoot list with its root
      removed / restricted / cut off. WinVerifyTrust still says Valid
      here, so this is the "trusted on THIS machine but not by
      Microsoft's program" trap, and only the AuthRoot gate can catch it.
#>
param([string]$GameDir = 'C:\_Games\Steam\steamapps\common\7 Days To Die')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SacCheck.ps1')

$work = Join-Path ([IO.Path]::GetTempPath()) ("saccal_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$failures = 0

function Expect($name, $file, $table, $verdict, [string[]]$mustFail = @()) {
    $r = Test-SacFile $file $table
    $failed = @($r.Checks | Where-Object Level -eq 'FAIL' | ForEach-Object Check)
    $missing = @($mustFail | Where-Object { $failed -notcontains $_ })
    $ok = ($r.Verdict -eq $verdict) -and -not $missing
    if (-not $ok) { $script:failures++ }
    Write-Host ("{0}  {1,-58} -> {2}" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $name, $r.Verdict) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    if (-not $ok) {
        if ($missing) { Write-Host "      expected these checks to fail: $($missing -join '; ')" -ForegroundColor Red }
        $r.Checks | ForEach-Object { Write-Host ("      {0,-4} {1}: {2}" -f $_.Level, $_.Check, $_.Detail) }
    }
}

function Get-RootThumb($file) {
    $leaf = (Get-AuthenticodeSignature -LiteralPath $file).SignerCertificate
    $c = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    $c.ChainPolicy.RevocationMode = 'NoCheck'; [void]$c.Build($leaf)
    $c.ChainElements[$c.ChainElements.Count - 1].Certificate.Thumbprint.ToUpper()
}

# a copy of the AuthRoot table with one root's entry edited (or removed: $edit = $null)
function Edit-Root($authRoot, $thumb, [scriptblock]$edit) {
    $roots = @{}
    foreach ($k in $authRoot.Roots.Keys) { $roots[$k] = $authRoot.Roots[$k] }
    if ($edit) { $e = $roots[$thumb].Clone(); & $edit $e; $roots[$thumb] = $e } else { $roots.Remove($thumb) }
    [pscustomobject]@{ Roots = $roots; Source = 'mutated' }
}

try {
    $authRoot = Get-AuthRootTable
    Write-Host "AuthRoot: $($authRoot.Roots.Count) roots ($($authRoot.Source))`n"

    $unity  = Join-Path $GameDir 'UnityPlayer.dll'
    $valve  = Join-Path $GameDir 'steamclient64.dll'
    $cmm    = Join-Path $GameDir 'Mods\ContextMusicManager\Libs\CMM_Decoder.dll'
    foreach ($f in $unity, $valve, $cmm) { if (-not (Test-Path $f)) { throw "control file missing: $f" } }

    Write-Host '-- ground truth'
    Expect 'UnityPlayer.dll (loads under SAC)'                     $unity $authRoot 'ALLOW'
    Expect 'steamclient64.dll (cert expired 2024, timestamped)'     $valve $authRoot 'ALLOW'
    if ((Get-AuthenticodeSignature -LiteralPath $cmm).Status -eq 'NotSigned') {
        Expect 'CMM_Decoder.dll unsigned (user saw error 4551)'     $cmm $authRoot 'BLOCK' @('embedded Authenticode signature')
    }

    Write-Host "`n-- constructed negatives"
    $tamper = Join-Path $work 'UnityPlayer_tampered.dll'
    $b = [IO.File]::ReadAllBytes($unity); $b[[int]($b.Length / 2)] = $b[[int]($b.Length / 2)] -bxor 0xFF
    [IO.File]::WriteAllBytes($tamper, $b)
    Expect 'signed DLL, one byte changed after signing'             $tamper $authRoot 'BLOCK' @('signature intact')

    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe |
        Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
    if ($signtool) {
        $rsa = New-Object System.Security.Cryptography.RSACng 3072
        $req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
            'CN=CMM Self-Signed Test', $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $eku = New-Object System.Security.Cryptography.OidCollection
        [void]$eku.Add((New-Object System.Security.Cryptography.Oid '1.3.6.1.5.5.7.3.3'))
        $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($eku, $false)))
        $cert = $req.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(30))
        $pfx = Join-Path $work 'selfsigned.pfx'
        [IO.File]::WriteAllBytes($pfx, $cert.Export('Pfx', 'calib'))
        $self = Join-Path $work 'CMM_Decoder_selfsigned.dll'
        Copy-Item $cmm $self
        & $signtool.FullName sign /q /f $pfx /p calib /fd SHA256 $self | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'signtool could not sign the self-signed control' }
        Expect 'CMM_Decoder signed with a self-made cert, no timestamp' $self $authRoot 'BLOCK' @(
            "root in Microsoft's Trusted Root Program", 'timestamped')
    } else { Write-Host 'skip  self-signed control (no signtool - install the Windows SDK)' -ForegroundColor Yellow }

    Write-Host "`n-- the AuthRoot gate alone (file is Valid on this PC; only Microsoft's list changes)"
    $g4 = Get-RootThumb $unity
    Expect 'root missing from the program'                          $unity (Edit-Root $authRoot $g4 $null) 'BLOCK' @(
        "root in Microsoft's Trusted Root Program")
    Expect 'root restricted to server auth (no Code Signing)'       $unity (Edit-Root $authRoot $g4 { param($e) $e.Eku = @('1.3.6.1.5.5.7.3.1') }) 'BLOCK' @(
        'root enabled for Code Signing')
    Expect 'root NotBefore 2020 for Code Signing (leaf issued 2023)' $unity (Edit-Root $authRoot $g4 { param($e) $e.NotBefore = [datetime]'2020-01-01'; $e.NotBeforeEku = @('1.3.6.1.5.5.7.3.3') }) 'BLOCK' @(
        'root not cut off (NotBefore)')
    Expect 'root NotBefore 2020 but only for server auth'           $unity (Edit-Root $authRoot $g4 { param($e) $e.NotBefore = [datetime]'2020-01-01'; $e.NotBeforeEku = @('1.3.6.1.5.5.7.3.1') }) 'ALLOW'
    Expect 'root disabled 2020 (signed 2025)'                       $unity (Edit-Root $authRoot $g4 { param($e) $e.Disallowed = [datetime]'2020-01-01' }) 'BLOCK' @(
        'root not cut off (Disallowed)')
} finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }

Write-Host ''
if ($failures) { Write-Host "$failures calibration case(s) FAILED - do not trust SacCheck verdicts" -ForegroundColor Red; exit 1 }
Write-Host 'calibration passed - SacCheck verdicts match every known case' -ForegroundColor Green
exit 0
