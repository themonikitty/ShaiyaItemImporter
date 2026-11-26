@echo off
echo ============================================
echo   Shaiya Item Importer - Test Import (10 items)
echo ============================================
echo.

cd /d "C:\Users\Gustavo.Machado\Desktop\LW PROJETOS\#SERVIDORES\Shaiya Core Stable v1.4\Tools\ShaiyaItemImporter\bin\Release\net8.0-windows"

start /wait /b "" ShaiyaItemImporter.exe --silent ^
  --donor "C:\Users\Gustavo.Machado\Downloads\Shaiya Essentials v16\Client\data.sah" ^
  --target "C:\Users\Gustavo.Machado\Desktop\LW PROJETOS\#SERVIDORES\Shaiya Core Stable v1.4\Shaiya Core Client\data.sah" ^
  --sql "Server=127.0.0.1;Database=PS_GameDefs;User Id=shaiya;Password=shaiya123;TrustServerCertificate=True;" ^
  --auto ^
  --limit 10

echo.
echo Exit code: %ERRORLEVEL%
pause