# Pre-commit safety check: makes sure the files that would be committed contain no secrets, no third-party game or
# console files and nothing unexpectedly large. Run from the repository root:
#   powershell -File tools/check-repo-hygiene.ps1
# Exit code 0 = clean, 1 = findings (printed). Only files git would include (tracked + untracked, not ignored) are checked.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$files = @(git ls-files --cached --others --exclude-standard) | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) }
Write-Output "Checking $($files.Count) files..."

$findings = New-Object System.Collections.Generic.List[string]

# 1. Extensions that indicate game images, console containers, keys or credentials.
$forbiddenExtensions = '.iso', '.cue', '.nca', '.xci', '.nsp', '.pkg', '.sfo', '.pbp', '.self', '.sprx', '.prx', '.rvz', '.wbfs', '.gcm', '.dol',
    '.pem', '.pfx', '.p12', '.key', '.snk', '.env', '.db', '.sqlite', '.sqlite3', '.jsonl', '.pdb', '.exe', '.dll'
foreach ($f in $files) {
    $ext = [System.IO.Path]::GetExtension($f).ToLowerInvariant()
    if ($forbiddenExtensions -contains $ext) { $findings.Add("forbidden file type: $f") }
    if ($f -match '(^|/)(bios|firmware|roms|dumps)/') { $findings.Add("forbidden folder: $f") }
    if ($f -match '(prod|title)\.keys$|keys\.txt$') { $findings.Add("looks like a key file: $f") }
}

# 2. Size: the repository should contain source, small samples and a few screenshots only.
foreach ($f in $files) {
    $len = (Get-Item $f).Length
    if ($len -gt 1MB) { $findings.Add("large file ({0:N0} KiB): $f" -f ($len / 1KB)) }
}

# 3. Secret patterns in text files.
$patterns = @(
    @{ Name = 'GitHub token';           Regex = '\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b' },
    @{ Name = 'GitHub fine-grained PAT'; Regex = '\bgithub_pat_[A-Za-z0-9_]{30,}\b' },
    @{ Name = 'private key block';       Regex = '-----BEGIN [A-Z ]*PRIVATE KEY-----' },
    @{ Name = 'AWS access key id';       Regex = '\bAKIA[0-9A-Z]{16}\b' },
    @{ Name = 'Slack token';             Regex = '\bxox[baprs]-[A-Za-z0-9-]{10,}\b' },
    @{ Name = 'password assignment';     Regex = '(?i)\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?token)\b\s*[:=]\s*["''][^"'']{6,}["'']' },
    @{ Name = 'connection string secret'; Regex = '(?i)(Password|Pwd)=[^;\s]{4,};' }
)
$textExtensions = '.cs', '.xaml', '.json', '.md', '.ps1', '.csproj', '.props', '.sln', '.yml', '.yaml', '.txt', '.gitignore', '.gitattributes', '.xml', '.config', ''
foreach ($f in $files) {
    $ext = [System.IO.Path]::GetExtension($f).ToLowerInvariant()
    if ($textExtensions -notcontains $ext) { continue }
    $text = [System.IO.File]::ReadAllText((Resolve-Path $f))
    foreach ($p in $patterns) {
        if ($text -match $p.Regex) { $findings.Add("possible $($p.Name) in $f") }
    }
}

# 4. Binary files other than the known samples and images.
foreach ($f in $files) {
    $ext = [System.IO.Path]::GetExtension($f).ToLowerInvariant()
    if ($ext -in '.elf', '.bin') {
        if ($f -notmatch '^samples/') { $findings.Add("binary outside samples/: $f") }
    }
}

if ($findings.Count -eq 0) {
    Write-Output 'OK: no secrets, no forbidden file types and no oversized files found.'
    exit 0
}

Write-Output 'FINDINGS:'
$findings | ForEach-Object { Write-Output "  - $_" }
exit 1
