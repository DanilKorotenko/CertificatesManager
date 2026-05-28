
for /f "tokens=*" %%i in ('git rev-list --count HEAD') do set buildNumber=%%i

git tag %buildNumber%

git push origin tag %buildNumber%

cd CertificatesManager

rmdir /s /q bin

rmdir /s /q obj

dotnet publish -r win-x64 --self-contained

cd Installer

set installerName=certificatesManager-%buildNumber%

set installerFileName=%installerName%.msi

echo %installerFileName%

@REM  dotnet tool install --global wix

wix build -arch x64 -d BuildNumber=%buildNumber% .\Product.wxs -out %installerFileName%

move "%installerFileName%" "../../"

del certificatesManager-%buildNumber%.wixpdb
