@echo off
cd /d "%~dp0"
if not exist EmotionCat.exe (
    echo EmotionCat.exe is missing. Extract the complete release package first.
    exit /b 1
)
start "" "%~dp0EmotionCat.exe"
