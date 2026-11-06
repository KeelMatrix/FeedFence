# Evaluated input companion for synthetic parser/CLI fixtures. Real restore
# equivalence and graph mutations are exercised separately by FeedFenceCliTests.
function Write-EvaluatedRestoreFixture([string]$ProjectPath, [hashtable]$Frameworks) {
    $project = [IO.Path]::GetFullPath($ProjectPath)
    $restoreFrameworks = @{}
    foreach ($framework in $Frameworks.Keys) {
        $restoreFrameworks[$framework] = @{ projectReferences = @{} }
    }
    $evidence = @{
        format = 1
        restore = @{ $project = @{} }
        projects = @{
            $project = @{
                frameworks = $Frameworks
                restore = @{
                    projectStyle = 'PackageReference'
                    projectName = [IO.Path]::GetFileNameWithoutExtension($project)
                    originalTargetFrameworks = @($Frameworks.Keys)
                    frameworks = $restoreFrameworks
                }
            }
        }
    }
    $obj = Join-Path (Split-Path -Parent $project) 'obj'
    New-Item -ItemType Directory -Force -Path $obj | Out-Null
    $path = Join-Path $obj ([IO.Path]::GetFileName($project) + '.nuget.dgspec.json')
    $evidence | ConvertTo-Json -Depth 20 -Compress | Set-Content -LiteralPath $path -Encoding utf8
}
