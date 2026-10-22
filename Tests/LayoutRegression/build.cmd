@echo off
rtk proxy "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe" Tests\LayoutRegression\LayoutRegression.csproj /t:Build /p:WarningLevel=0 /v:minimal /nologo
