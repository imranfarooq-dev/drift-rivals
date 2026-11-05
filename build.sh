#!/bin/zsh
# Rebuilds Builds/DriftRivals.app (close the Unity Editor first if it has this project open)
cd "$(dirname "$0")"
/Applications/Unity/Hub/Editor/6000.3.28f1/Unity.app/Contents/MacOS/Unity -batchmode -quit -projectPath . -executeMethod ProjectSetup.SetupAndBuild -logFile Logs/build.log
grep -E "error CS|Build result" Logs/build.log
