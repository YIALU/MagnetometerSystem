#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$KeyPath,
    [string]$Server = '59.110.232.32',
    [string]$User = 'root',
    [int]$Port = 22
)
$ErrorActionPreference = 'Stop'
$sourceKey = (Resolve-Path -LiteralPath $KeyPath).Path
$repository = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $repository ('.codex_tmp/feedback-token/' + [Guid]::NewGuid())
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = Get-Acl -LiteralPath $scratch
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
Set-Acl -LiteralPath $scratch -AclObject $acl
$temporaryKey = Join-Path $scratch 'ssh-key'
try {
    Copy-Item -LiteralPath $sourceKey -Destination $temporaryKey
    $fileAcl = Get-Acl -LiteralPath $temporaryKey
    $fileAcl.SetAccessRuleProtection($true, $false)
    $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
    Set-Acl -LiteralPath $temporaryKey -AclObject $fileAcl
    # Interactive SSH TTY hides the token. It is never passed in arguments, saved locally or logged.
    $command = @'
set -eu
token_tmp=$(mktemp /etc/magnetometer-feedback/token.XXXXXX)
trap 'stty echo; rm -f "$token_tmp"' EXIT
printf 'Paste the repository-scoped GitHub token (hidden), then press Enter: '
stty -echo
IFS= read -r feedback_token
stty echo
printf '\n'
if [ -z "$feedback_token" ]; then echo 'No token provided; configuration unchanged.'; exit 1; fi
printf '%s' "$feedback_token" > "$token_tmp"
unset feedback_token
chown root:magnetometer-feedback "$token_tmp"
chmod 0640 "$token_tmp"
mv "$token_tmp" /etc/magnetometer-feedback/github-token
printf 'Configured. Pending feedback will sync in the background.\n'
'@
    & ssh -tt -i $temporaryKey -p $Port -o IdentitiesOnly=yes -o StrictHostKeyChecking=ask "$User@$Server" $command
    if ($LASTEXITCODE -ne 0) { throw 'Server configuration failed.' }
}
finally {
    if (Test-Path -LiteralPath $temporaryKey) { Remove-Item -LiteralPath $temporaryKey }
    Remove-Item -LiteralPath $scratch
}
