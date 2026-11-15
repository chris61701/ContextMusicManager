<#
.SYNOPSIS
  Offline emulation of Windows 11 Smart App Control's verdict on the native
  binaries a release ships. Runs on Windows 10; needs no Windows 11.

.DESCRIPTION
  Smart App Control (SAC) allows a native image (DLL/EXE) that the cloud has no
  reputation for ONLY IF it is "publicly trusted signed code" - Microsoft:
    "Smart App Control allows only publicly trusted signed code or unsigned code
     that the Intelligent Security Graph (ISG) predicts to be safe."
    "Publicly trusted signed code means the signing certificate's issuer is one
     of the certificate authorities (CA) in Microsoft's Trusted Root Program."
  A mod DLL with a few thousand downloads and a new hash every release never
  gets an ISG verdict, so for CMM the signature is the WHOLE gate. That gate is
  deterministic, so it can be checked offline:

    1. the file carries an embedded Authenticode signature
    2. the signature is intact (file not modified after signing) and chains
    3. the chain's root is in Microsoft's Trusted Root Program (AuthRoot CTL),
       NOT merely trusted on this machine (a self-signed or corporate root that
       you installed locally passes Windows' own check but fails SAC)
    4. the root program enables that root for Code Signing, and has not cut it
       off (NotBefore / Disallowed dates) for this certificate
    5. the signature is timestamped (else it dies with the cert - Azure
       Artifact Signing certs live ~3 days)
    plus warnings: SHA-1 file digest, non-RSA signer key (App Control signer
    rules are RSA-only).

  Managed (.NET) assemblies are reported but not judged: 7 Days to Die's Mono
  runtime reads them as data, so Code Integrity never sees them (proven in the
  field - the SAC user's log was written BY the unsigned managed CMM DLL).

  The AuthRoot list comes from the copy Windows keeps in the registry (synced by
  Windows Update), or with -Online from Microsoft's published authrootstl.cab.

  What this does NOT emulate: the ISG cloud reputation path (irrelevant for a
  file with no reputation) and anything Windows 11 adds beyond the documented
  rule. Calibrate.ps1 proves the checker against known-good and known-bad files.

.PARAMETER Path
  Files, folders (searched recursively) or .zip release archives.

.EXAMPLE
  powershell -File tools\SacCheck\SacCheck.ps1 dist\ContextMusicManager-*.zip
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments)]
    [string[]]$Path,
    [switch]$Online,
    [switch]$PassThru
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$OID_CODESIGN = '1.3.6.1.5.5.7.3.3'
$OID_RSA      = '1.2.840.113549.1.1.1'
$OID_SHA1     = '1.3.14.3.2.26'
$OID_RFC3161  = '1.3.6.1.4.1.311.3.3.1'
$OID_CSIG     = '1.2.840.113549.1.9.6'
$OID_SIGTIME  = '1.2.840.113549.1.9.5'

# ---------------------------------------------------------------- AuthRoot CTL
function Get-AuthRootTable {
    param([switch]$Online)
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("sac_" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        $stl = Join-Path $tmp 'authroot.stl'
        if ($Online) {
            $cab = Join-Path $tmp 'authrootstl.cab'
            Invoke-WebRequest -UseBasicParsing -OutFile $cab `
                'http://ctldl.windowsupdate.com/msdownload/update/v3/static/trustedr/en/authrootstl.cab'
            & expand.exe $cab $stl | Out-Null
            $src = 'authrootstl.cab (downloaded now)'
        } else {
            $k = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\SystemCertificates\AuthRoot\AutoUpdate'
            [IO.File]::WriteAllBytes($stl, $k.EncodedCtl)
            $sync = if ($k.PSObject.Properties['LastSyncTime']) {
                [DateTime]::FromFileTime([BitConverter]::ToInt64($k.LastSyncTime, 0)) } else { 'unknown' }
            $src = "registry copy, last synced $sync"
        }
        $dump = & certutil.exe -dump $stl
        if ($LASTEXITCODE -ne 0) { throw "certutil could not parse the AuthRoot list" }

        $table = @{}; $cur = $null; $prop = 0
        foreach ($line in $dump) {
            if ($line -match '^Subject Identifier: ([0-9a-f]{40})') {
                $cur = @{ Name = '?'; Eku = $null; NotBefore = $null; NotBeforeEku = $null
                          Disallowed = $null; DisallowedEku = $null }
                $table[$Matches[1].ToUpper()] = $cur; $prop = 0; continue
            }
            if (-not $cur) { continue }
            if ($line -match 'Attribute\[\d+\]: 1\.3\.6\.1\.4\.1\.311\.10\.11\.(\d+)') { $prop = [int]$Matches[1]; continue }
            if ($line -match '^\s+Value\[') { continue }
            $oid = if ($line -match '\((\d+(?:\.\d+)+)\)\s*$') { $Matches[1] } else { $null }
            switch ($prop) {
                9   { if ($oid) { if (-not $cur.Eku) { $cur.Eku = @() }; $cur.Eku += $oid } }
                127 { if ($oid) { if (-not $cur.NotBeforeEku) { $cur.NotBeforeEku = @() }; $cur.NotBeforeEku += $oid } }
                122 { if ($oid) { if (-not $cur.DisallowedEku) { $cur.DisallowedEku = @() }; $cur.DisallowedEku += $oid } }
                126 { if ($line.Trim() -and -not $cur.NotBefore)  { $cur.NotBefore  = [DateTime]::Parse($line.Trim()) } }
                104 { if ($line.Trim() -and -not $cur.Disallowed) { $cur.Disallowed = [DateTime]::Parse($line.Trim()) } }
                11  { if ($line.Trim()) { $cur.Name = $line.Trim(); $prop = 0 } }
            }
        }
        if ($table.Count -lt 100) { throw "AuthRoot list parsed to only $($table.Count) roots - parser is broken" }
        [pscustomobject]@{ Roots = $table; Source = $src }
    } finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- PE helpers
function Get-PeInfo([string]$file) {
    $b = [IO.File]::ReadAllBytes($file)
    if ($b.Length -lt 0x40 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { return $null }
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    if ($pe -le 0 -or $pe + 0x100 -gt $b.Length -or [BitConverter]::ToUInt32($b, $pe) -ne 0x4550) { return $null }
    $opt = $pe + 24
    $dd  = if ([BitConverter]::ToUInt16($b, $opt) -eq 0x20b) { $opt + 112 } else { $opt + 96 }
    $secOff = [BitConverter]::ToInt32($b, $dd + 4 * 8); $secLen = [BitConverter]::ToInt32($b, $dd + 4 * 8 + 4)
    $pkcs7 = $null
    if ($secOff -gt 0 -and $secLen -gt 8 -and $secOff + $secLen -le $b.Length) {
        $len = [BitConverter]::ToInt32($b, $secOff)          # WIN_CERTIFICATE.dwLength
        $pkcs7 = New-Object byte[] ($len - 8)
        [Array]::Copy($b, $secOff + 8, $pkcs7, 0, $len - 8)
    }
    [pscustomobject]@{ Managed = ([BitConverter]::ToInt32($b, $dd + 14 * 8) -ne 0); Pkcs7 = $pkcs7 }
}

# minimal DER walk: children of the constructed value starting at $at
function Get-DerChildren([byte[]]$d, [int]$at) {
    $i = $at + 1; $l = [int]$d[$i++]
    if ($l -band 0x80) { $n = $l -band 0x7F; $l = 0; 1..$n | ForEach-Object { $l = ($l -shl 8) -bor $d[$i++] } }
    $end = $i + $l; $kids = @()
    while ($i -lt $end) {
        $s = $i; $t = $d[$i++]; $cl = [int]$d[$i++]
        if ($cl -band 0x80) { $n = $cl -band 0x7F; $cl = 0; 1..$n | ForEach-Object { $cl = ($cl -shl 8) -bor $d[$i++] } }
        $kids += [pscustomobject]@{ Tag = $t; Start = $s; Body = $i; Len = $cl }; $i += $cl
    }
    $kids
}

function Get-SignatureDetails([byte[]]$pkcs7) {
    $r = [pscustomobject]@{ Digest = $null; TimeStamp = $null; Certs = $null }
    $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms
    $cms.Decode($pkcs7)
    $r.Certs = $cms.Certificates
    $si = $cms.SignerInfos[0]
    $r.Digest = $si.DigestAlgorithm
    foreach ($a in $si.UnsignedAttributes) {
        if ($a.Oid.Value -eq $OID_RFC3161) {
            try {
                $ts = New-Object System.Security.Cryptography.Pkcs.SignedCms
                $ts.Decode($a.Values[0].RawData)
                [byte[]]$tst = $ts.ContentInfo.Content
                if ($tst[0] -eq 0x04) {   # some runtimes leave the eContent OCTET STRING wrapper on
                    $h = 2; if ($tst[1] -band 0x80) { $h += $tst[1] -band 0x7F }
                    $tst = $tst[$h..($tst.Length - 1)]
                }
                $g = Get-DerChildren $tst 0 | Where-Object Tag -eq 0x18 | Select-Object -First 1
                $s = [Text.Encoding]::ASCII.GetString($tst, $g.Body, $g.Len) -replace '\.\d+', ''
                $r.TimeStamp = [DateTime]::ParseExact($s, 'yyyyMMddHHmmss\Z', $null,
                    [Globalization.DateTimeStyles]'AssumeUniversal,AdjustToUniversal')
            } catch { $r.TimeStamp = [DateTime]::MinValue }   # present but unparsed
        } elseif ($a.Oid.Value -eq $OID_CSIG) {
            $r.TimeStamp = [DateTime]::MinValue
            foreach ($cs in $si.CounterSignerInfos) {
                foreach ($sa in $cs.SignedAttributes) {
                    if ($sa.Oid.Value -eq $OID_SIGTIME) {
                        $r.TimeStamp = ([System.Security.Cryptography.Pkcs.Pkcs9SigningTime]$sa.Values[0]).SigningTime.ToUniversalTime()
                    }
                }
            }
        }
    }
    $r
}

# ---------------------------------------------------------------- the verdict
function Test-SacFile([string]$file, $authRoot) {
    $checks = New-Object System.Collections.Generic.List[object]
    function Add-Check($level, $what, $detail) { $checks.Add([pscustomobject]@{ Level = $level; Check = $what; Detail = $detail }) }

    $pe = Get-PeInfo $file
    if (-not $pe) { return [pscustomobject]@{ File = $file; Kind = 'not-pe'; Verdict = 'SKIP'; Checks = @() } }
    if ($pe.Managed) {
        Add-Check 'INFO' 'managed assembly' 'Mono loads it as data; Code Integrity never sees it'
        return [pscustomobject]@{ File = $file; Kind = 'managed'; Verdict = 'SKIP'; Checks = $checks }
    }

    $sig = Get-AuthenticodeSignature -LiteralPath $file
    if (-not $pe.Pkcs7) {
        $why = if ($sig.SignatureType -eq 'Catalog') { 'catalog-signed only - catalogs do not travel with a mod zip' } else { 'no signature at all' }
        Add-Check 'FAIL' 'embedded Authenticode signature' $why
        return [pscustomobject]@{ File = $file; Kind = 'native'; Verdict = 'BLOCK'; Checks = $checks }
    }
    Add-Check 'PASS' 'embedded Authenticode signature' 'present'

    $det = Get-SignatureDetails $pe.Pkcs7
    $leaf = $sig.SignerCertificate
    if (-not $leaf) { $leaf = $det.Certs | Where-Object { $_.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } } | Select-Object -First 1 }

    switch ($sig.Status) {
        'Valid'        { Add-Check 'PASS' 'signature intact and trusted here' 'WinVerifyTrust: Valid' }
        'HashMismatch' { Add-Check 'FAIL' 'signature intact' 'file was MODIFIED after signing' }
        default        { Add-Check 'FAIL' 'signature trusted here' "WinVerifyTrust: $($sig.Status) - $($sig.StatusMessage)" }
    }

    # the time Windows judges the chain at: the timestamp if there is one, else now
    $vt = if ($det.TimeStamp -and $det.TimeStamp -ne [DateTime]::MinValue) { $det.TimeStamp.ToLocalTime() } else { Get-Date }

    if ($leaf) {
        $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
        $chain.ChainPolicy.RevocationMode = 'NoCheck'
        $chain.ChainPolicy.VerificationTime = $vt
        $chain.ChainPolicy.VerificationFlags = 'AllowUnknownCertificateAuthority'
        [void]$chain.ChainPolicy.ApplicationPolicy.Add((New-Object System.Security.Cryptography.Oid $OID_CODESIGN))
        $chain.ChainPolicy.ExtraStore.AddRange($det.Certs)
        [void]$chain.Build($leaf)
        $root = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
        $selfSigned = $root.Subject -eq $root.Issuer
        $entry = $authRoot.Roots[$root.Thumbprint.ToUpper()]
        $rootName = "$($root.GetNameInfo('SimpleName', $false)) [$($root.Thumbprint.Substring(0,8))]"

        if (-not $selfSigned) {
            Add-Check 'FAIL' 'chain reaches a root' "stops at $rootName - an intermediate is missing from the signature"
        } elseif (-not $entry) {
            Add-Check 'FAIL' "root in Microsoft's Trusted Root Program" "$rootName is NOT in the program (self-signed / locally installed / private CA)"
        } else {
            Add-Check 'PASS' "root in Microsoft's Trusted Root Program" $entry.Name
            if ($entry.Eku -and ($entry.Eku -notcontains $OID_CODESIGN)) {
                Add-Check 'FAIL' 'root enabled for Code Signing' "program restricts this root to: $($entry.Eku -join ', ')"
            } else { Add-Check 'PASS' 'root enabled for Code Signing' 'yes' }
            if ($entry.NotBefore -and (-not $entry.NotBeforeEku -or $entry.NotBeforeEku -contains $OID_CODESIGN) -and $leaf.NotBefore -gt $entry.NotBefore) {
                Add-Check 'FAIL' 'root not cut off (NotBefore)' "program distrusts code-signing certs issued after $($entry.NotBefore.ToString("yyyy-MM-dd")); this one was issued $($leaf.NotBefore.ToString("yyyy-MM-dd"))"
            }
            if ($entry.Disallowed -and (-not $entry.DisallowedEku -or $entry.DisallowedEku -contains $OID_CODESIGN) -and $vt -gt $entry.Disallowed) {
                Add-Check 'FAIL' 'root not cut off (Disallowed)' "root disabled after $($entry.Disallowed.ToString("yyyy-MM-dd")); signature judged at $($vt.ToString("yyyy-MM-dd"))"
            }
        }
        Add-Check 'INFO' 'signer' "$($leaf.GetNameInfo('SimpleName', $false)), cert valid $($leaf.NotBefore.ToString("yyyy-MM-dd")) .. $($leaf.NotAfter.ToString("yyyy-MM-dd"))"
        if ($leaf.PublicKey.Oid.Value -ne $OID_RSA) {
            Add-Check 'WARN' 'signer key is RSA' "key is $($leaf.PublicKey.Oid.FriendlyName); App Control signer rules are RSA-only"
        }
    }

    if (-not $det.TimeStamp) {
        Add-Check 'FAIL' 'timestamped' 'no timestamp - the signature becomes invalid the moment the cert expires'
    } elseif ($det.TimeStamp -eq [DateTime]::MinValue) {
        Add-Check 'PASS' 'timestamped' 'yes (time not decoded)'
    } else { Add-Check 'PASS' 'timestamped' ("{0:u}" -f $det.TimeStamp) }

    if ($det.Digest.Value -eq $OID_SHA1) { Add-Check 'WARN' 'file digest SHA-256' 'SHA-1 digest - deprecated' }

    $verdict = if ($checks | Where-Object Level -eq 'FAIL') { 'BLOCK' } else { 'ALLOW' }
    [pscustomobject]@{ File = $file; Kind = 'native'; Verdict = $verdict; Checks = $checks }
}

# ---------------------------------------------------------------- main
# Dot-sourced (Calibrate.ps1) = load the functions only.
if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $Path) { throw 'usage: SacCheck.ps1 <file|folder|zip> [...] [-Online]' }

$authRoot = Get-AuthRootTable -Online:$Online
$cleanup = @(); $targets = @()
foreach ($p in $Path) {
    foreach ($item in (Get-Item -Path $p)) {
        if ($item.PSIsContainer) {
            $targets += Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Include *.dll, *.exe | Select-Object -ExpandProperty FullName
        } elseif ($item.Extension -eq '.zip') {
            $x = Join-Path ([IO.Path]::GetTempPath()) ("sacz_" + [Guid]::NewGuid().ToString('N'))
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [IO.Compression.ZipFile]::ExtractToDirectory($item.FullName, $x); $cleanup += $x
            $targets += Get-ChildItem -LiteralPath $x -Recurse -File -Include *.dll, *.exe | Select-Object -ExpandProperty FullName
        } else { $targets += $item.FullName }
    }
}

$results = @()
try {
    Write-Host "Smart App Control emulation - AuthRoot: $($authRoot.Roots.Count) roots ($($authRoot.Source))"
    foreach ($t in $targets) {
        $r = Test-SacFile $t $authRoot
        $results += $r
        if ($r.Kind -eq 'not-pe') { continue }
        $show = $t; foreach ($c in $cleanup) { $show = $show.Replace($c, '<zip>') }
        $color = @{ ALLOW = 'Green'; BLOCK = 'Red'; SKIP = 'DarkGray' }[$r.Verdict]
        Write-Host ("{0,-6} {1}" -f $r.Verdict, $show) -ForegroundColor $color
        if ($r.Kind -eq 'native') {
            foreach ($c in $r.Checks) {
                $cc = @{ PASS = 'DarkGreen'; FAIL = 'Red'; WARN = 'Yellow'; INFO = 'Gray' }[$c.Level]
                Write-Host ("         {0,-4} {1}: {2}" -f $c.Level, $c.Check, $c.Detail) -ForegroundColor $cc
            }
        }
    }
} finally { foreach ($c in $cleanup) { Remove-Item -Recurse -Force $c -ErrorAction SilentlyContinue } }

$native = @($results | Where-Object Kind -eq 'native')
$blocked = @($native | Where-Object Verdict -eq 'BLOCK')
Write-Host ("{0} native file(s): {1} would be BLOCKED, {2} allowed" -f $native.Count, $blocked.Count, ($native.Count - $blocked.Count))
if ($PassThru) { $results }
if ($blocked.Count) { exit 1 } else { exit 0 }
