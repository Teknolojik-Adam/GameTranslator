GameTranslator .NET Framework 4.8 + our .NET8 ONNX OCR.
Compile bridge: dotnet publish src/GameTranslatorOCR.Bridge/GameTranslatorOCR.Bridge.csproj -c Release -r win-x64 --self-contained false -o win-x64
Ship OwnOCR/win-x64 and OwnOCR/models next to the application .exe. Requires .NET8 runtime.
