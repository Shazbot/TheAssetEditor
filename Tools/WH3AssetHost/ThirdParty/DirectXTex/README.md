# DirectXTex texconv

WH3AssetHost uses Microsoft's `texconv.exe` from DirectXTex for painted DDS
output so the exported texture can preserve the source DDS/DXGI format.

Pinned upstream release: `may2026` (DirectXTex 2026.5.8)

Official binary:
https://github.com/microsoft/DirectXTex/releases/download/may2026/texconv.exe

Expected SHA-512:
`aeaf50e4e531182ff44c3879fa68b6fbf43eda69a9c3e632e9a6d5ae74a8239301c406391ac0a9d4b9a1579f8a0571360ac1e20f01559997f644e19fd33ac4d9`

The WH3AssetHost project downloads and verifies that exact binary when it is
missing, then copies it next to WH3AssetHost.exe for build/publish output.

DirectXTex is MIT licensed. See LICENSE.txt in this directory.
