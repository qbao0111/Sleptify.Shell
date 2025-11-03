param([string]$Runtime="win-x64",[string]$Configuration="Release")
dotnet publish "$PSScriptRoot\..\Sleptify.Shell\Sleptify.Shell.csproj" -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PSScriptRoot\..\publish"
Write-Host "Published to: $PSScriptRoot\..\publish"
