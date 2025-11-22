# Fix namespace issues

# Fix PhotoCli.Core files - remove extra spaces
$coreFiles = Get-ChildItem -Path "src\PhotoCli.Core" -Filter "*.cs" -Recurse -Exclude "*.g.cs"

foreach ($file in $coreFiles) {
    $content = Get-Content $file.FullName -Raw
    
    # Fix trailing spaces after namespace
    $content = $content -replace 'namespace PhotoCli\.Core\.Runners\s+', 'namespace PhotoCli.Core.Runners'
    $content = $content -replace 'namespace PhotoCli\.Core\.Services\s+', 'namespace PhotoCli.Core.Services'
    $content = $content -replace 'namespace PhotoCli\.Core\.Models\s+', 'namespace PhotoCli.Core.Models'
    $content = $content -replace 'namespace PhotoCli\.Core\.Utils\s+', 'namespace PhotoCli.Core.Utils'
    $content = $content -replace 'namespace PhotoCli\.Core\.Config\s+', 'namespace PhotoCli.Core.Config'
    
    Set-Content $file.FullName -Value $content -NoNewline
}

# Fix PhotoCli.Console files - remove extra spaces
$consoleFiles = Get-ChildItem -Path "src\PhotoCli.Console" -Filter "*.cs" -Recurse -Exclude "*.g.cs"

foreach ($file in $consoleFiles) {
    $content = Get-Content $file.FullName -Raw
    
    # Fix trailing spaces
    $content = $content -replace 'namespace PhotoCli\.Console\.Runners\s+', 'namespace PhotoCli.Console.Runners'
    $content = $content -replace 'namespace PhotoCli\.Console\.Options\s+', 'namespace PhotoCli.Console.Options'
    
    Set-Content $file.FullName -Value $content -NoNewline
}

Write-Host "Namespace fixes complete!" -ForegroundColor Green
