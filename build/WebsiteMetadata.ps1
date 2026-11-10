function Assert-WebsiteJsonFields($Element, [string[]]$Names) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Website metadata requires a JSON object.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if ($property.Name -cnotin $Names -or -not $seen.Add($property.Name)) {
            throw "Unknown or duplicate website metadata field: $($property.Name)"
        }
    }
    if ($seen.Count -ne $Names.Count) { throw 'Website metadata is missing a required field.' }
}

function Get-WebsiteClassification([string]$Tags) {
    $tokens = @($Tags -split '[;\s]+' | Where-Object { $_.Length -gt 0 })
    $result = @{}
    foreach ($group in @(
            @{ Name = 'visibility'; Values = @('public-product', 'internal-package') },
            @{ Name = 'role'; Values = @('primary', 'component') }
        )) {
        $allowed = @($group.Values | ForEach-Object { "keelmatrix-$_" })
        $found = @($tokens | Where-Object { $_ -in $allowed })
        if ($found.Count -ne 1 -or $found[0] -cnotin $allowed) {
            throw "PackageTags must contain exactly one lowercase $($group.Name) sentinel."
        }
        $result[$group.Name] = $found[0].Substring('keelmatrix-'.Length)
    }
    return $result
}

function Assert-WebsitePackageUrls($Package) {
    $canonical = 'https://github.com/KeelMatrix/FeedFence'
    if ($Package.PackageProjectUrl -cne $canonical -or $Package.RepositoryUrl -cne $canonical -or $Package.RepositoryType -cne 'git') {
        throw 'Package project/repository metadata must identify the canonical https://github.com/KeelMatrix/FeedFence git repository.'
    }
}

function Assert-WebsiteMetadata([string]$ManifestPath, [object[]]$Packages) {
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($ManifestPath))
    try {
        $root = $document.RootElement
        Assert-WebsiteJsonFields $root @('schemaVersion', 'packages')
        if ($root.GetProperty('schemaVersion').GetRawText() -cne '1') { throw 'Website metadata requires schemaVersion 1.' }
        $entries = $root.GetProperty('packages')
        if ($entries.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Website packages must be an object.' }
        $byId = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $entries.EnumerateObject()) {
            if (-not $ids.Add($entry.Name)) { throw 'Duplicate website package ID.' }
            Assert-WebsiteJsonFields $entry.Value @('visibility', 'role')
            $visibility = $entry.Value.GetProperty('visibility').GetString()
            $role = $entry.Value.GetProperty('role').GetString()
            if ($visibility -cnotin @('public-product', 'internal-package') -or $role -cnotin @('primary', 'component')) {
                throw 'Unknown website visibility or role.'
            }
            $byId.Add($entry.Name, @{ visibility = $visibility; role = $role })
        }
        if ($Packages.Count -eq 0 -or $byId.Count -ne $Packages.Count) { throw 'Website manifest and packable project IDs must match exactly.' }
        $ids.Clear()
        foreach ($package in $Packages) {
            if ([string]::IsNullOrWhiteSpace($package.PackageId) -or -not $ids.Add($package.PackageId) -or -not $byId.ContainsKey($package.PackageId)) {
                throw 'Missing, duplicate or case-incorrect packable package ID in website manifest.'
            }
            Assert-WebsitePackageUrls $package
            $classification = Get-WebsiteClassification $package.PackageTags
            $entry = $byId[$package.PackageId]
            if ($classification.visibility -cne $entry.visibility -or $classification.role -cne $entry.role) {
                throw 'PackageTags classification disagrees with website manifest.'
            }
        }
        $primaryCount = @($byId.Values | Where-Object { $_.role -ceq 'primary' }).Count
        $publicCount = @($byId.Values | Where-Object { $_.visibility -ceq 'public-product' }).Count
        $publicPrimaryCount = @($byId.Values | Where-Object { $_.visibility -ceq 'public-product' -and $_.role -ceq 'primary' }).Count
        if ($primaryCount -gt 1 -or ($publicCount -gt 0 -and $publicPrimaryCount -ne 1)) {
            throw 'A public product family requires exactly one primary package.'
        }
    }
    finally { $document.Dispose() }
}

function Get-WebsiteSourcePackages([string]$Root, [string]$Configuration) {
    $properties = 'IsPackable,PackageId,PackageTags,PackageProjectUrl,RepositoryUrl,RepositoryType'
    $projects = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.*proj' | Where-Object {
            $_.Extension -in @('.csproj', '.fsproj', '.vbproj') -and
            $_.FullName.Substring($Root.Length) -notmatch '[/\\](?:bin|obj|artifacts|\.nuget|\.git)[/\\]'
        })
    foreach ($project in $projects) {
        $output = & dotnet msbuild $project.FullName -nologo "-p:Configuration=$Configuration" "-getProperty:$properties"
        if ($LASTEXITCODE -ne 0) { throw "Could not evaluate website package metadata for $($project.Name)." }
        $evaluated = ($output -join "`n" | ConvertFrom-Json).Properties
        if ($evaluated.IsPackable -ieq 'true') { $evaluated }
    }
}

function Get-WebsiteNuspecValue($Metadata, [string]$Name) {
    $nodes = @($Metadata.SelectNodes("*[local-name()='$Name']"))
    if ($nodes.Count -ne 1) { throw "Built package requires exactly one nuspec $Name element." }
    return $nodes[0]
}

function Assert-WebsiteBuiltPackage([string]$Path, [object[]]$Packages) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($entries.Count -ne 1) { throw 'Built package requires exactly one nuspec.' }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $entries[0].Open()
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try {
            $document = [Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
        }
        finally { $reader.Dispose(); $stream.Dispose() }
        $metadata = @($document.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']"))
        if ($metadata.Count -ne 1) { throw 'Built package requires exactly one metadata element.' }
        $metadata = $metadata[0]
        $id = (Get-WebsiteNuspecValue $metadata 'id').InnerText
        $source = @($Packages | Where-Object { $_.PackageId -ceq $id })
        if ($source.Count -ne 1) { throw 'Built package ID does not match a packable project.' }
        $repository = Get-WebsiteNuspecValue $metadata 'repository'
        $built = @{
            PackageId = $id
            PackageTags = (Get-WebsiteNuspecValue $metadata 'tags').InnerText
            PackageProjectUrl = (Get-WebsiteNuspecValue $metadata 'projectUrl').InnerText
            RepositoryUrl = $repository.GetAttribute('url')
            RepositoryType = $repository.GetAttribute('type')
        }
        Assert-WebsitePackageUrls $built
        $classification = Get-WebsiteClassification $built.PackageTags
        $sourceClassification = Get-WebsiteClassification $source[0].PackageTags
        if ($classification.visibility -cne $sourceClassification.visibility -or $classification.role -cne $sourceClassification.role) {
            throw 'Built package classification disagrees with source.'
        }
        $sourceTags = @($source[0].PackageTags -split '[;\s]+' | Where-Object { $_ } | Sort-Object -CaseSensitive)
        $builtTags = @($built.PackageTags -split '[;\s]+' | Where-Object { $_ } | Sort-Object -CaseSensitive)
        if (($sourceTags -join "`n") -cne ($builtTags -join "`n")) { throw 'Built package tags disagree with source.' }
    }
    finally { $archive.Dispose() }
}
