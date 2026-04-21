# 清理旧输出
Remove-Item -Path "./publish" -Recurse -Force -ErrorAction SilentlyContinue

# 发布为单文件exe
dotnet publish src/MagnetometerSystem.App/MagnetometerSystem.App.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o ./publish

Write-Host "打包完成！输出路径: ./publish/MagnetometerSystem.App.exe" -ForegroundColor Green
