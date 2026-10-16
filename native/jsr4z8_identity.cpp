// Generator for src/Jsr4Z8.UI/jsr4z8.native-identity.dll - the product identity
// marker NativeExtractSweep looks for in .net\<exe name>\<bundle-id>\ caches.
//
// The single-file bundler only embeds real PE files (an 83-byte text file named
// *.dll is left loose next to the exe - verified), and a default cl /LD stub is
// ~100 KB of CRT we do not need, so this is the smallest honest PE: no CRT, no
// imports, an exported no-op. The result is COMMITTED at
// src/Jsr4Z8.UI/jsr4z8.native-identity.dll and shipped via a csproj None item -
// do not add a build step for it. Regenerate (size should stay a few KB):
//   cl /nologo /LD /O2 /GS- /Zl jsr4z8_identity.cpp /Fe:jsr4z8.native-identity.dll /link /NODEFAULTLIB /ENTRY:DllMain /OPT:REF /OPT:ICF

extern "C" __declspec(dllexport) void __stdcall jsr4z8_native_identity_marker(void) {}

extern "C" int __stdcall DllMain(void*, unsigned, void*) { return 1; }
