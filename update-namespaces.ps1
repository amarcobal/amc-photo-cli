# Namespace Reorganization Script

# Update PhotoCli.Core files
$coreFiles = Get-ChildItem -Path "src\PhotoCli.Core" -Filter "*.cs" -Recurse

foreach ($file in $coreFiles) {
    $content = Get-Content $file.FullName -Raw
    $content = $content -replace 'namespace PhotoCli\.Models', 'namespace PhotoCli.Core.Models'
    $content = $content -replace 'namespace PhotoCli\.Services', 'namespace PhotoCli.Core.Services'
    $content = $content -replace 'namespace PhotoCli\.Utils', 'namespace PhotoCli.Core.Utils'
    $content = $content -replace 'namespace PhotoCli\.Config', 'namespace PhotoCli.Core.Config'
    Set-Content $file.FullName -Value $content -NoNewline
}

# Update PhotoCli.Console files
$consoleFiles = Get-ChildItem -Path "src\PhotoCli.Console" -Filter "*.cs" -Recurse

foreach ($file in $consoleFiles) {
    $content = Get-Content $file.FullName -Raw
    
    # Update namespace declarations
    $content = $content -replace 'namespace PhotoCli\.Runners', 'namespace PhotoCli.Console.Runners '
    $content = $content -replace 'namespace PhotoCli\.Options', 'namespace PhotoCli.Console.Options'
    $content = $content -replace 'namespace PhotoCli;', 'namespace PhotoCli.Console;'
    
    # Update global usings
    $content = $content -replace 'global using PhotoCli\.Models', 'global using PhotoCli.Core.Models'
    $content = $content -replace 'global using PhotoCli\.Services', 'global using PhotoCli.Core.Services'
    $content = $content -replace 'global using PhotoCli\.Utils', 'global using PhotoCli.Core.Utils'
    $content = $content -replace 'global using PhotoCli\.Options', 'global using PhotoCli.Console.Options'
    $content = $content -replace 'global using PhotoCli\.Runners', 'global using PhotoCli.Console.Runners'
    
    # Update regular usings
    $content = $content -replace 'using PhotoCli\.Models', 'using PhotoCli.Core.Models'
    $content = $content -replace 'using PhotoCli\.Services', 'using PhotoCli.Core.Services'
    $content = $content -replace 'using PhotoCli\.Utils', 'using PhotoCli.Core.Utils'
    
    Set-Content $file.FullName -Value $content -NoNewline
}

Write-Host "Namespace update complete!" -ForegroundColor Green
