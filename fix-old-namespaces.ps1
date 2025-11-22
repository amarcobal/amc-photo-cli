# Fix remaining old namespace references in PhotoCli.Core

$coreFiles = Get-ChildItem -Path "src\PhotoCli.Core" -Filter "*.cs" -Recurse -Exclude "*.g.cs", "*.Designer.cs"

foreach ($file in $coreFiles) {
    $content = Get-Content $file.FullName -Raw
    $modified = $false
    
    # Fix old using statements
    if ($content -match 'using PhotoCli\.Models;') {
        $content = $content -replace 'using PhotoCli\.Models;', 'using PhotoCli.Core.Models;'
        $modified = $true
    }
    if ($content -match 'using PhotoCli\.Services;') {
        $content = $content -replace 'using PhotoCli\.Services;', 'using PhotoCli.Core.Services;'
        $modified = $true
    }
    if ($content -match 'using PhotoCli\.Utils;') {
        $content = $content -replace 'using PhotoCli\.Utils;', 'using PhotoCli.Core.Utils;'
        $modified = $true
    }
    if ($content -match 'using PhotoCli\.Options;') {
        # Options moved to Console, but some Core files might reference it
        # We'll need to handle this case by case or add project reference
        Write-Host "Warning: $($file.FullName) references PhotoCli.Options" -ForegroundColor Yellow
    }
    
    if ($modified) {
        Set-Content $file.FullName -Value $content -NoNewline
        Write-Host "Fixed: $($file.Name)" -ForegroundColor Green
    }
}

Write-Host "`nOld namespace fix complete!" -ForegroundColor Green
