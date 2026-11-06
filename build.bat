@echo off
rem Rebuilds NbaQuant.exe (desktop) and nbaquant-cli.exe (command line) with the C# compiler that ships with Windows.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=/r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll
set SRC=src\Common.cs src\Espn.cs src\Net.cs src\Predictions.cs src\Data.cs src\Sim.cs src\Analysis.cs src\Report.cs src\Sheets.cs src\Notes.cs src\Store.cs src\Engine.cs src\Learn.cs src\Matchups.cs src\Standouts.cs src\Audit.cs src\BoxScores.cs src\Review.cs src\Bank.cs src\Site.cs src\Safest.cs src\InjuryReport.cs src\App.cs src\Gui.cs
cd /d "%~dp0"
"%CSC%" /nologo /optimize+ /platform:x64 /nowarn:0649,0162 /target:winexe /out:NbaQuant.exe %REFS% %SRC% || exit /b 1
"%CSC%" /nologo /optimize+ /platform:x64 /nowarn:0649,0162 /target:exe /out:nbaquant-cli.exe %REFS% %SRC% || exit /b 1
echo Built NbaQuant.exe and nbaquant-cli.exe
