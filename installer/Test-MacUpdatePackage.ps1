param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$taskArchive = [System.IO.Compression.ZipFile]::OpenRead($taskPackage)
try {
    $taskPlists = @($taskArchive.Entries | Where-Object {
        $_.FullName -match '(^|/)Info\.plist$'
    })
    $taskHasScheme = $false
    foreach ($taskEntry in $taskPlists) {
        $taskStream = $taskEntry.Open()
        try {
            $taskXml = [System.Xml.XmlDocument]::new()
            $taskXml.Load($taskStream)
            # CFBundleURLSchemes sits inside the dictionary CFBundleURLTypes holds, not at /plist/dict directly, so the
            # query starts from the key wherever it is found and reads the array that follows it.
            if ($taskXml.SelectSingleNode("//key[text()='CFBundleURLSchemes']/following-sibling::array[1]/string[text()='axmolhub']")) {
                $taskHasScheme = $true
                break
            }
        }
        finally {
            $taskStream.Dispose()
        }
    }

    if (-not $taskHasScheme) {
        throw 'The macOS full update package must contain an app Info.plist declaring the axmolhub URL scheme.'
    }
    $taskHasCodeResources = $taskArchive.Entries | Where-Object {
        $_.FullName -match '(^|/)_CodeSignature/CodeResources$'
    } | Select-Object -First 1
    if (-not $taskHasCodeResources) {
        throw 'The macOS full update package must contain a bundle code signature (_CodeSignature/CodeResources).'
    }
}
finally {
    $taskArchive.Dispose()
}
