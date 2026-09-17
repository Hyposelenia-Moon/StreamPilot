# 读取 .git 对象（zlib/deflate）的辅助函数，用于在无法执行 git.exe 时对比历史版本。
function Read-GitObject {
    param([Parameter(Mandatory)][string]$Sha, [Parameter(Mandatory)][string]$GitDir)

    $path = Join-Path $GitDir ("objects\{0}\{1}" -f $Sha.Substring(0, 2), $Sha.Substring(2))
    if (-not (Test-Path -LiteralPath $path)) {
        throw "object not found: $Sha"
    }

    $bytes = [IO.File]::ReadAllBytes($path)
    $stream = New-Object IO.MemoryStream(, $bytes[2..($bytes.Length - 1)])
    $deflate = New-Object IO.Compression.DeflateStream($stream, [IO.Compression.CompressionMode]::Decompress)
    $buffer = New-Object IO.MemoryStream
    $deflate.CopyTo($buffer)
    $deflate.Close()
    $raw = $buffer.ToArray()

    $nul = [Array]::IndexOf($raw, [byte]0)
    $header = [Text.Encoding]::ASCII.GetString($raw, 0, $nul)
    $parts = $header.Split(' ')
    return [pscustomobject]@{
        Type = $parts[0]
        Body = $raw[($nul + 1)..($raw.Length - 1)]
        Header = $header
    }
}

function Get-GitTreeEntry {
    param([Parameter(Mandatory)][string]$TreeSha, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$GitDir)

    $obj = Read-GitObject -Sha $TreeSha -GitDir $GitDir
    $text = [Text.Encoding]::UTF8.GetString($obj.Body)
    $cursor = 0
    while ($cursor -lt $text.Length) {
        $space = $text.IndexOf(' ', $cursor)
        $nul = $text.IndexOf([char]0, $space)
        $mode = $text.Substring($cursor, $space - $cursor)
        $entryName = $text.Substring($space + 1, $nul - $space - 1)
        $sha = ($text.Substring($nul + 1, 40))
        if ($entryName -eq $Name) {
            return [pscustomobject]@{ Mode = $mode; Sha = $sha }
        }
        $cursor = $nul + 41
    }
    throw "entry not found: $Name in $TreeSha"
}

function Get-GitFileContent {
    param([Parameter(Mandatory)][string]$CommitSha, [Parameter(Mandatory)][string[]]$PathParts, [Parameter(Mandatory)][string]$GitDir)

    $commit = Read-GitObject -Sha $CommitSha -GitDir $GitDir
    $commitText = [Text.Encoding]::UTF8.GetString($commit.Body)
    $treeSha = ([regex]::Match($commitText, '(?m)^tree ([0-9a-f]{40})$')).Groups[1].Value
    $current = $treeSha
    foreach ($part in $PathParts) {
        $entry = Get-GitTreeEntry -TreeSha $current -Name $part -GitDir $GitDir
        $current = $entry.Sha
    }
    $blob = Read-GitObject -Sha $current -GitDir $GitDir
    return [Text.Encoding]::UTF8.GetString($blob.Body)
}
